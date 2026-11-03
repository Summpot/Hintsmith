if (args.FirstOrDefault() == "weave") Console.WriteLine(Hintsmith.Tasks.HintingWeaver.Rewrite(args[1], typeof(Hintsmith.Hinting).Assembly.Location, args[2]));
else if (args.FirstOrDefault() == "symbols") SymbolTests.Run(args.Skip(1).ToArray());
else if (args.FirstOrDefault() == "verify-symbols") SymbolTests.Verify(args[1], args[2]);
else ContractTests.Run(args);
