using MMW.Cli;

MMW.Core.AppDataFolders.MigrateLegacyFolders();
return await CommandLine.RunAsync(args, Console.Out, Console.Error);
