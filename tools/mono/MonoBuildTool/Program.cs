// The Mono build's Cecil steps (tools/mono/build_mono.py runs them; each is one of the Mono workarounds).
//
//   MonoBuildTool strip-fnptr-mods <in.dll> <out.dll>   workaround 3
//   MonoBuildTool rewrite-callables <in.dll> <out.dll>  workarounds 7 and 10
//   MonoBuildTool verify-godotsharp <GodotSharp.dll>    prints the facts the check script tests, as JSON
//   MonoBuildTool list-pinvokes <dll>...                prints "<library>\t<entry point>", sorted and unique
//   MonoBuildTool check-game-references <sts2.dll> <folder for its dependencies> <dll>...
//                                                       every game reference in the dlls exists in the real game
//   MonoBuildTool check-assembly-references <publish folder> <names absent by design, comma separated> <root dll>...
//                                                       every assembly the roots need is in the publish folder
using STS2Mobile.Tools.Mono;

try
{
    switch (args)
    {
        case ["strip-fnptr-mods", var input, var output]:
            Console.WriteLine(GodotSharpRewrites.StripFunctionPointerModifiers(input, output));
            return 0;
        case ["rewrite-callables", var input, var output]:
            Console.WriteLine(GodotSharpRewrites.RewriteCallables(input, output));
            return 0;
        case ["verify-godotsharp", var path]:
            Console.WriteLine(GodotSharpRewrites.Verify(path));
            return 0;
        case ["list-pinvokes", .. var paths] when paths.Length > 0:
            foreach (var line in PInvokeList.Entries(paths))
                Console.WriteLine(line);
            return 0;
        case ["check-game-references", var game, var folder, .. var paths] when paths.Length > 0:
            var missing = GameReferences.Missing(game, [folder], paths);
            foreach (var line in missing)
                Console.WriteLine(line);
            return missing.Count == 0 ? 0 : 1;
        case ["check-assembly-references", var publish, var absent, .. var roots] when roots.Length > 0:
            var gone = AssemblyReferences.Missing(publish, absent.Split(','), roots);
            foreach (var line in gone)
                Console.WriteLine(line);
            return gone.Count == 0 ? 0 : 1;
        default:
            Console.Error.WriteLine("Usage: MonoBuildTool strip-fnptr-mods|rewrite-callables|verify-godotsharp|list-pinvokes|check-game-references|check-assembly-references ...");
            return 2;
    }
}
catch (InvalidOperationException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
