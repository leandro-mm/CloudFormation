using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;
using Moq;
using Xunit;

namespace AWSLambda.Tests;

public class FunctionTest
{
    [Fact]
    public async Task Create_AMI_From_EC2_Instance_Returns_AMI_ID()
    {
        // Arrange
        var instanceId = "i-1234567890abcdef0";
        var sourceRegion = "us-east-1";
        var amiId = "ami-abcdef1234567890";
        
        var ec2Mock = new Mock<IAmazonEC2>();
        var lambdaLoggerMock = new Mock<ILambdaLogger>();

        // Mock the Config property to return a valid region endpoint
        var ec2Config = new AmazonEC2Config { RegionEndpoint = RegionEndpoint.USEast1 };
        ec2Mock.Setup(ec2 => ec2.Config).Returns(ec2Config);

        // Mock DescribeImages to always return no existing pending AMIs
        ec2Mock.Setup(e => e.DescribeImagesAsync(It.IsAny<DescribeImagesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeImagesResponse
            {
                Images = []
            });

        // Mock CreateImage to return a fake ImageId
        ec2Mock.Setup(e => e.CreateImageAsync(It.IsAny<CreateImageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateImageResponse
            {
                ImageId = amiId
            });

        lambdaLoggerMock.Setup(x => x.Log(It.IsAny<string>()))
            .Callback<string>(msg => Console.WriteLine($"[LOG] {msg}"));

        // On subsequent DescribeImages call, simulate that the AMI becomes 'available'
        var firstCall = true;
        ec2Mock.Setup(e => e.DescribeImagesAsync(It.Is<DescribeImagesRequest>(r => r.ImageIds.Contains(amiId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    return new DescribeImagesResponse
                    {
                        Images = new List<Image> { new Image { ImageId = amiId, State = nameof(StatusEnum.Pending).ToLower() } }
                    };
                }
                else
                {
                    return new DescribeImagesResponse
                    {
                        Images = new List<Image> { new Image { ImageId = amiId, State = nameof(StatusEnum.Available).ToLower() } }
                    };
                }
            });

        
        lambdaLoggerMock.Setup(x => x.LogLine(It.IsAny<string>()))
            .Callback<string>(msg => Console.WriteLine($"[LambdaLog] {msg}"));

        // Act
        var amiCreator = new AmiCreator(instanceId, sourceRegion, lambdaLoggerMock.Object,ec2Mock.Object, ec2Mock.Object);
        var result = await amiCreator.RunAsync();

        // Assert
        Assert.Equal("200", result["statusCode"]);
        Assert.Equal("ami-abcdef1234567890", result["ami_id"]);
        Assert.Equal(sourceRegion, result["source_region"]);
    }

    enum StatusEnum
    {
        Pending,
        Available
    }
}
