using Microsoft.Extensions.Logging;
using ScumStudio.Core.Logging;

namespace ScumStudio.Tests.Core;

public sealed class LoggingTests
{
    [Fact]
    public void WritesOnlyEnabledLevels()
    {
        var writer = new StringWriter();
        using (var factory = new TextWriterLoggerFactory(writer, LogLevel.Warning))
        {
            var logger = factory.CreateLogger("Test");
            logger.LogInformation("hidden");
            logger.LogWarning("shown {Value}", 42);
        }

        var text = writer.ToString();
        Assert.DoesNotContain("hidden", text);
        Assert.Contains("WRN Test: shown 42", text);
    }
}
