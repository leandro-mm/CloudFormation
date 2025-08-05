using System.Runtime.CompilerServices;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Lambda.Core;
using Microsoft.Extensions.Logging;

namespace AWSLambda
{
    public class AmiCreator
    {
        #region Fields

        private readonly IAmazonEC2 _ec2Source;
        private readonly IAmazonEC2 _ec2Target;
        private readonly string _sourceRegion;
        private readonly string _targetRegion;
        private readonly int _maxLambdaTime;
        private readonly int _checkInterval;
        private readonly ILambdaLogger _logger;
        private readonly DateTime _startTime;


        #endregion

        #region Properties

        public string InstanceId { get; }

        #endregion


        #region Construtors

        public AmiCreator(string instanceId, string? targetRegion, ILambdaLogger logger)
        {
            _logger = logger;

            _ec2Source = new AmazonEC2Client(); // Use current region
            _sourceRegion = _ec2Source.Config.RegionEndpoint.SystemName;
            _targetRegion = targetRegion ?? _sourceRegion;

            _ec2Target = _targetRegion != _sourceRegion
                ? new AmazonEC2Client(Amazon.RegionEndpoint.GetBySystemName(_targetRegion))
                : _ec2Source;

            _maxLambdaTime = int.TryParse(Environment.GetEnvironmentVariable("MAX_LAMBDA_TIME"), out var maxTime)
                ? maxTime
                : 840;
            _checkInterval = int.TryParse(Environment.GetEnvironmentVariable("CHECK_INTERVAL"), out var interval)
                ? interval
                : 30;

            _startTime = DateTime.UtcNow;

            InstanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
        }

        public AmiCreator(string instanceId, string? targetRegion, ILambdaLogger logger, IAmazonEC2 ec2Source, IAmazonEC2 ec2Target)
            : this(instanceId, targetRegion, logger)
        {
            _ec2Source = ec2Source;
            _ec2Target = ec2Target;
        }

        #endregion


        private double GetRemainingTime()
            => Math.Max(0, _maxLambdaTime - (DateTime.UtcNow - _startTime).TotalSeconds);

        private async Task<string?> CheckExistingAMIAsync()
        {
            var describeRequest = new DescribeImagesRequest
            {
                Filters =
                [
                    new Filter("state", ["pending"]),
                    new Filter("image-type", ["machine"])
                ]
            };

            var result = await _ec2Source.DescribeImagesAsync(describeRequest);

            return (from image in result.Images
                where image.Tags.Exists(tag => tag.Key == "InstanceId" && tag.Value == InstanceId)
                select image.ImageId).FirstOrDefault();
        }

        private async Task<bool> WaitForExistingAMICompletionAsync()
        {
            var remaining = GetRemainingTime();
            while (remaining > _checkInterval)
            {
                var existingAmi = await CheckExistingAMIAsync();
                if (existingAmi == null)
                    return true;

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(_checkInterval, remaining)));
                remaining = GetRemainingTime();
            }

            return false;
        }

        public async Task<Dictionary<string, string>> RunAsync()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var amiDescription = $"AMI from instance-{InstanceId} (created {timestamp})";
            var uniqueAmiName = $"AMI-{InstanceId}-{timestamp}";
            string? targetAmiId = null;

            try
            {
                if (!await WaitForExistingAMICompletionAsync())
                    throw new TimeoutException("Timeout waiting for existing AMI operation");

                var createImageRequest = new CreateImageRequest
                {
                    InstanceId = InstanceId,
                    Name = uniqueAmiName,
                    Description = amiDescription,
                    NoReboot = true
                };

                var createImageResponse = await _ec2Source.CreateImageAsync(createImageRequest);
                var imageId = createImageResponse.ImageId;

                _logger.LogInformation($"AMI {imageId} creation started");

                await WaitForAmiAvailableAsync(imageId);

                var message = $"Successfully created AMI {imageId} on {_sourceRegion}";

                if (_targetRegion != _sourceRegion && GetRemainingTime() > 120)
                {
                    try
                    {
                        var copyRequest = new CopyImageRequest
                        {
                            Name = uniqueAmiName,
                            SourceImageId = imageId,
                            SourceRegion = _sourceRegion,
                            Description = amiDescription
                        };

                        var copyResponse = await _ec2Target.CopyImageAsync(copyRequest);
                        targetAmiId = copyResponse.ImageId;

                        _logger.LogInformation($"AMI copied to {_targetRegion}: {targetAmiId}");
                        message += $" and copied to {_targetRegion} as {targetAmiId}";
                    }
                    catch (Exception e)
                    {
                        _logger.LogError($"Failed to copy AMI: {e}");
                        message += $", but copying to {_targetRegion} failed: {e.Message}";
                    }
                }
                else if (_targetRegion != _sourceRegion)
                {
                    _logger.LogWarning("Skipping AMI copy due to insufficient remaining time");
                    message += $", but copying to {_targetRegion} was skipped (insufficient time)";
                }

                return new Dictionary<string, string>
                {
                    { "statusCode", "200" },
                    { "body", message },
                    { "ami_id", imageId },
                    { "source_region", _sourceRegion },
                    { "target_region", _targetRegion }
                };
            }
            catch (AmazonEC2Exception ec2Ex)
            {
                _logger.LogError($"AWS EC2 error: {ec2Ex}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unhandled error: {ex}");
                throw;
            }
            finally
            {
                _logger.LogInformation(
                    $"Total execution time: {(DateTime.UtcNow - _startTime).TotalSeconds:F2} seconds");
            }
        }

        private async Task WaitForAmiAvailableAsync(string imageId)
        {
            while (true)
            {
                var describeResponse = await _ec2Source.DescribeImagesAsync(new DescribeImagesRequest
                {
                    ImageIds = new List<string> { imageId }
                });

                var state = describeResponse.Images.FirstOrDefault()?.State;

                if (state == ImageState.Available)
                {
                    _logger.LogInformation($"AMI {imageId} is now available.");
                    break;
                }

                if (state == ImageState.Failed)
                {
                    throw new Exception($"AMI creation failed for image {imageId}.");
                }

                _logger.LogInformation($"AMI {imageId} is in state '{state}', waiting {_checkInterval} seconds...");
                await Task.Delay(TimeSpan.FromSeconds(_checkInterval));

                if (GetRemainingTime() < _checkInterval)
                {
                    throw new TimeoutException("Timeout waiting for AMI to become available.");
                }
            }
        }
    }
}