using GhCLI.Protocol;

namespace GhCLI;

internal static class CliExitCodes
{
    public const int Success = 0;
    public const int RuntimeFailure = 1;
    public const int PluginUnavailable = 2;
    public const int Validation = 3;
    public const int SolveTimeout = 4;

    public static int FromResponse(RpcResponseEnvelope? response)
    {
        if (response?.Ok == true)
        {
            return Success;
        }

        return response?.Error?.Code switch
        {
            "plugin_unavailable" => PluginUnavailable,
            "validation" => Validation,
            "solve_timeout" => SolveTimeout,
            _ => RuntimeFailure
        };
    }
}
