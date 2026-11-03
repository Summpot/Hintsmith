using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;

namespace Hintsmith.Tasks;

internal sealed class MethodOffsetMap
{
    private readonly MethodDefinition _method;
    private readonly Dictionary<int, CilInstruction> _original;
    private readonly int _oldSize;
    private readonly Dictionary<CilInstruction, CilInstruction> _replacements;
    public Dictionary<int, int> SourceOffsets { get; private set; } = new();
    public Dictionary<int, int> Boundaries { get; private set; } = new();
    public List<(int Start, int End, int? NextOldOffset)> Insertions { get; } = new();
    public bool Changed { get; private set; }

    public MethodOffsetMap(MethodDefinition method, Dictionary<CilInstruction, CilInstruction> replacements)
    {
        _method = method;
        _replacements = replacements;
        var instructions = method.CilMethodBody!.Instructions;
        instructions.CalculateOffsets();
        _original = instructions.ToDictionary(i => i.Offset);
        _oldSize = instructions.Count == 0 ? 0 : instructions.Last().Offset + instructions.Last().Size;
    }

    public void Complete()
    {
        var instructions = _method.CilMethodBody!.Instructions;
        instructions.CalculateOffsets();
        var oldByNewInstruction = new Dictionary<CilInstruction, int>();
        foreach (var pair in _original)
        {
            var actual = _replacements.TryGetValue(pair.Value, out var replacement) ? replacement : pair.Value;
            SourceOffsets[pair.Key] = actual.Offset;
            Boundaries[pair.Key] = pair.Key == 0 ? 0 : pair.Value.Offset;
            oldByNewInstruction[actual] = pair.Key;
        }
        var size = instructions.Count == 0 ? 0 : instructions.Last().Offset + instructions.Last().Size;
        Boundaries[_oldSize] = size;
        int? start = null;
        foreach (var instruction in instructions)
        {
            if (!oldByNewInstruction.TryGetValue(instruction, out var oldOffset)) start ??= instruction.Offset;
            else if (start.HasValue)
            {
                Insertions.Add((start.Value, instruction.Offset, oldOffset));
                start = null;
            }
        }
        if (start.HasValue) Insertions.Add((start.Value, size, null));
        Changed = Insertions.Count != 0 || size != _oldSize || SourceOffsets.Any(p => p.Key != p.Value);
    }

    public int Boundary(int offset) => Boundaries.TryGetValue(offset, out var result)
        ? result : throw new NotSupportedException($"PDB offset {offset} is not an IL boundary in {_method.FullName}.");
}
