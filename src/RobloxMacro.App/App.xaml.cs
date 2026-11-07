using System;
using System.Windows;

namespace RobloxMacro.App;

public partial class App : Application
{
    public static bool IsSmokeTestMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        foreach (string arg in e.Args)
        {
            if (arg.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase))
            {
                IsSmokeTestMode = true;
            }
        }

        base.OnStartup(e);
    }
}
