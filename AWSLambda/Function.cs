using System.Text.Json;
using Amazon.Lambda.Core;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace AWSLambda;

public class Function
{

    /// <summary>
    /// function to call 
    /// </summary>
    /// <param name="input">The event for the Lambda function handler to process. expecting: { "instance_id": "...", "target_region": "..." }</param>
    /// <param name="context">The ILambdaContext that provides methods for logging and describing the Lambda environment.</param>
    /// <returns></returns>
    public async Task<string> FunctionHandler(string input, ILambdaContext context)
    {
        var request = JsonSerializer.Deserialize<AmiRequest>(input);

        if (request == null || string.IsNullOrEmpty(request.InstanceId))
        {
            context.Logger.LogError("Invalid input: InstanceId is required.");
            throw new ArgumentException("Invalid input: InstanceId is required.");
        }

        var creator = new AmiCreator(request.InstanceId, request.TargetRegion, context.Logger);
        var result = await creator.RunAsync();

        return JsonSerializer.Serialize(result); 
    }
}
