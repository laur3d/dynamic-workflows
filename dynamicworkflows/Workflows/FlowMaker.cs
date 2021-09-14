namespace dynamicworkflows.Workflows
{
    using System;
    using Jint;
    using Newtonsoft.Json;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Jint.Parser;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.DurableTask;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Logging;

    public class DynamicStep<T, U>
    {
        public string Action { get; private set; }
        public U param { get; private set; }
        public string Fn { get; }

        public List<DynamicStep<T, U>> LeftSteps { get; private set; }


        public List<DynamicStep<T, U>> RightSteps { get; private set; }

        public DynamicStep(string action, U param)
        {
            Action = action;
            this.param = param;
            Fn = string.Empty;
        }

        public static DynamicStep<T, U> Branch(string condition, List<DynamicStep<T, U>> leftSteps, List<DynamicStep<T, U>> rightSteps, U param = default)
        {
            return new DynamicStep<T, U>(ActionName.Branch, param, condition, leftSteps, rightSteps);
        }

        // public DynamicStep(string fn, List<DynamicStep< T,U>> leftSteps, List<DynamicStep< T,U>> rightSteps, U param )
        // {
        //     Action = ActionName.Branch;
        //     this.param = param;
        //     Fn = fn;
        //     LeftSteps = leftSteps ?? new List<DynamicStep<T, U>>();
        //     RightSteps = rightSteps ?? new List<DynamicStep<T, U>>();
        // }

        [JsonConstructor]
        public DynamicStep(string action, U param, string fn,  List<DynamicStep< T,U>> leftSteps = default, List<DynamicStep< T,U>> rightSteps = default)
        {
            Action = action;
            this.param = param;
            Fn = fn;
            LeftSteps = leftSteps;
            RightSteps = rightSteps;
        }
    }

    public class DynamicResult<T>
    {
        public T Result { get; set; }
    }

    public static class ActionName
    {
        public const string Add = "Add";
        public const string Subtract = "Subtract";
        public const string Multiply = "Multiply";
        public const string Divide = "Divide";
        public const string Dynamic = "Dynamic";
        public const string Branch = "Branch";
    }


    public static class FlowMaker
    {
        [FunctionName("FlowMaker")]
        public static async Task<double> Run([OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var steps = new List<DynamicStep<double, double>>
            {
                new DynamicStep<double, double>(ActionName.Add, 1),
                new DynamicStep<double, double>(ActionName.Add, 2),
                new DynamicStep<double, double>(ActionName.Add, 3),
                new DynamicStep<double, double>(ActionName.Dynamic, 2, "(2 * r + 1)/p"), // <-- simulate loading for a datasource
                DynamicStep<double, double>.Branch("r % 2 == 0",
                    new List<DynamicStep<double, double>>
                    {
                        new DynamicStep<double, double>(ActionName.Divide, 2)
                    }, new List<DynamicStep<double, double>>() )
            };

            var ctx = new DynamicFlowContext
            {
                Steps = steps
            };

            var result = await context.CallSubOrchestratorAsync<DynamicResult<double>>("DynamicOrchestrator", ctx);
            return result.Result;
        }

        [FunctionName("DynamicOrchestrator")]
        public static async Task<DynamicResult<double>> RunInnerOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext ctx)
        {
            var input = ctx.GetInput<DynamicFlowContext>();
            double state = input.State != default? input.State : 0;

            foreach (var step in input.Steps)
            {
                if (step.Action == ActionName.Branch)
                {

                    var result = await ctx.CallSubOrchestratorAsync<double>("Branch", new BranchContext<double, double>()
                    {
                        State = state ,
                        DynamicStep = step
                    });

                    return new DynamicResult<double>
                    {
                        Result = Convert.ToDouble(result)
                    };
                }
                else
                {
                    state = await ctx.CallActivityAsync<double>(step.Action, new DynamicParam
                    {
                        Accumulator = state,
                        Parameter = step.param,
                        Fn = step.Fn,
                    });
                }
            }

            return new DynamicResult<double>
            {
                Result = state
            };
        }

        [FunctionName("Add")]
        public static async Task<double> Add([ActivityTrigger] DynamicParam param, ILogger log)
        {
            return param.Accumulator + param.Parameter;
        }

        [FunctionName("Subtract")]
        public static async Task<double> Subtract([ActivityTrigger] DynamicParam param, ILogger log)
        {
            return param.Accumulator - param.Parameter;
        }

        [FunctionName("Multiply")]
        public static async Task<double> Multiply([ActivityTrigger] DynamicParam param, ILogger log)
        {
            return param.Accumulator * param.Parameter;
        }

        [FunctionName("Divide")]
        public static async Task<double> Divide([ActivityTrigger] DynamicParam param, ILogger log)
        {
            return param.Accumulator / param.Parameter;
        }


        [FunctionName("Dynamic")]
        public static async Task<double> DynamicCalculate([ActivityTrigger] DynamicParam param, ILogger log)
        {
            var func = new Engine()
                .Execute($"function dyn(r, p){{ return {param.Fn} }}").GetValue("dyn");

            var invoked = func.Invoke(param.Accumulator, param.Parameter);

            double.TryParse(invoked.ToString(), out var result);
            return result;
        }

        [FunctionName("branch")]
        public static async Task<double> Branch([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger logger)
        {
            var input = context.GetInput<BranchContext<double, double>>();
            var param = input.DynamicStep;
            var newState = input.State;

            var branchToRun = await context.CallActivityAsync<bool>("BranchAction", new DynamicParam()
            {
                Accumulator = input.State,
                Fn = param.Fn,
                Parameter =param.param,
            });

            if (branchToRun)
            {
                if (param.LeftSteps != null && param.LeftSteps.Count > 0)
                {
                    return await context.CallSubOrchestratorAsync<double>("DynamicOrchestrator", new DynamicFlowContext()
                    {
                        State = newState,
                        Steps = param.LeftSteps
                    });
                }

                return newState;


            }
            else
            {
                if (param.RightSteps != null && param.RightSteps.Count > 0)
                {
                    return await context.CallSubOrchestratorAsync<double>("DynamicOrchestrator", new DynamicFlowContext()
                    {
                        State = newState,
                        Steps = param.RightSteps,
                    });
                }

                return newState;
            }


        }

        [FunctionName("BranchAction")]
        public static async Task<bool> DynamicBranchAction([ActivityTrigger] DynamicParam param, ILogger log)
        {
            var func = new Engine()
                .Execute($"function branch(r, p){{ return {param.Fn} }}").GetValue("branch");

            var invoked = func.Invoke(param.Accumulator , param.Parameter);
            bool.TryParse(invoked.ToString(), out var result);

            return result;
        }

        [FunctionName("FlowMaker_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "start")]
            HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("FlowMaker");

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }


        public class DynamicFlowContext
        {
            public List<DynamicStep<double, double>> Steps { get; set; }
            public double State { get; set; }
        }

        public class BranchContext<T, U>
        {
            public T State { get; set; }
            public DynamicStep<T, U> DynamicStep { get; set; }
        }

        public class DynamicParam
        {
            public double Accumulator { get; set; }
            public double Parameter { get; set; }
            public string Fn { get; set; }
        }
    }
}
