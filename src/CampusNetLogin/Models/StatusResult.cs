namespace CampusNetLogin.Models;

/// <summary>登录状态。</summary>
public enum ConnectionState
{
    Unknown,
    Checking,
    Online,
    Offline,
    Error,
}

/// <summary>一次状态查询的结果。</summary>
public sealed record StatusResult
{
    /// <summary>认证服务器是否可达。</summary>
    public bool Reachable { get; init; }

    /// <summary>是否已登录。</summary>
    public bool Online { get; init; }

    /// <summary>在线账号。</summary>
    public string Uid { get; init; } = string.Empty;

    /// <summary>客户端 IP。</summary>
    public string Ip { get; init; } = string.Empty;

    public static StatusResult Unreachable() => new() { Reachable = false, Online = false };
}

/// <summary>一次操作（登录/注销）的结果。</summary>
public sealed record OperationResult
{
    public bool Ok { get; init; }
    public bool Online { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;

    public static OperationResult Fail(string msg) => new() { Ok = false, Message = msg };
}
