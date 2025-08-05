using System.Text.Json.Serialization;

namespace AWSLambda
{
    internal class AmiRequest
    {
        [JsonPropertyName("instance_id")] public required string InstanceId { get; set; }

        [JsonPropertyName("target_region")] public string? TargetRegion { get; set; }
    }
}