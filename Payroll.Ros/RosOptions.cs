namespace Payroll.Ros;

public enum RosEnvironment
{
    /// <summary>ROS Public Interface Test - mirrors production. Requires separate PIT registration/cert.</summary>
    Pit,
    Production
}

public sealed class RosOptions
{
    public required string EmployerRegistrationNumber { get; init; }

    /// <summary>Any identifying name/version for this software - Revenue does not require pre-approval to use the REST API with your own ROS cert.</summary>
    public required string SoftwareUsed { get; init; }
    public required string SoftwareVersion { get; init; }

    public string? AgentTain { get; init; }

    public required string P12Path { get; init; }

    /// <summary>The plain password you type into ROS/your P12 tool - NOT the derived PKCS12 password. Derivation happens in <see cref="RosCertificateLoader"/>.</summary>
    public required string P12PlainPassword { get; init; }

    public RosEnvironment Environment { get; init; } = RosEnvironment.Pit;

    public Uri BaseAddress => Environment switch
    {
        RosEnvironment.Pit => new Uri("https://softwaretest.ros.ie/"),
        RosEnvironment.Production => new Uri("https://www.ros.ie/"),
        _ => throw new ArgumentOutOfRangeException()
    };
}
