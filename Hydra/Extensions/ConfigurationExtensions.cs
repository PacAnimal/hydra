using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace Hydra.Extensions;

public static class ConfigurationExtensions
{
    public static bool IsProduction(this IConfiguration _) => !Debugger.IsAttached;
}
