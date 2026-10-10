using System.Reflection;
using System.Reflection.Emit;

namespace DiScope.Agent;

/// <summary>
/// Minimal IL walker used to infer what a factory lambda resolves from the service provider
/// (e.g. <c>sp =&gt; new Gateway(sp.GetRequiredService&lt;IHttpClientFactory&gt;())</c>).
/// </summary>
internal static class ILReader
{
    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    static ILReader()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op) continue;
            var value = unchecked((ushort)op.Value);
            if (value < 0x100) OneByte[value] = op;
            else if ((value & 0xff00) == 0xfe00) TwoByte[value & 0xff] = op;
        }
    }

    internal readonly record struct Instruction(OpCode OpCode, int Token);

    /// <summary>Yields instructions that carry a metadata token or string (calls, ldtoken, ldstr, newobj).</summary>
    public static IEnumerable<Instruction> ReadTokenInstructions(MethodInfo method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { yield break; }
        if (il is null) yield break;

        var pos = 0;
        while (pos < il.Length)
        {
            OpCode op;
            var b = il[pos++];
            if (b == 0xfe)
            {
                if (pos >= il.Length) yield break;
                op = TwoByte[il[pos++]];
            }
            else
            {
                op = OneByte[b];
            }

            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    pos += 1;
                    break;
                case OperandType.InlineVar:
                    pos += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    pos += 8;
                    break;
                case OperandType.InlineSwitch:
                    if (pos + 4 > il.Length) yield break;
                    var count = BitConverter.ToInt32(il, pos);
                    pos += 4 + 4 * count;
                    break;
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.InlineString:
                case OperandType.InlineField:
                    if (pos + 4 > il.Length) yield break;
                    var token = BitConverter.ToInt32(il, pos);
                    pos += 4;
                    yield return new Instruction(op, token);
                    break;
                default: // InlineI, InlineBrTarget, InlineSig, ShortInlineR
                    pos += 4;
                    break;
            }
        }
    }
}
