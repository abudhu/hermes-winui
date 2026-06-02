using System;
using System.Windows.Forms;
using Hermes.TrayApp;

ApplicationConfiguration.Initialize();
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

using var ctx = new TrayAppContext();
Application.Run(ctx);
