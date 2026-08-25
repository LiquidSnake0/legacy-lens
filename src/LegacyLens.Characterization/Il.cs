using System.Reflection;
using System.Reflection.Emit;

namespace LegacyLens.Characterization;

/// <summary>
/// The constants a compiled method carries, read out of its instructions.
///
/// `if (years >= 3)` leaves the 3 in the method body. Reading it there is what
/// lets a characterization run use the boundaries the code was written around
/// when the source that code came from is not on this machine, which on an
/// inherited codebase is the normal case rather than the awkward one.
///
/// **Decoded rather than scanned.** An instruction stream is not a sequence of
/// bytes that can be searched for the ones that look like constants: an
/// operand can hold any value, so 0x20 sitting inside a four-byte token reads
/// as `ldc.i4` to anything that does not know where instructions begin. The
/// walk below steps instruction by instruction, and the length of each is
/// decided by its operand type.
///
/// The table is built from the runtime's own <see cref="OpCodes"/> rather than
/// written out here. A hand-copied opcode table is a list of two hundred
/// numbers that has to be right, and nothing would say when it was not.
/// </summary>
internal static class Il
{
    private static readonly Lazy<IReadOnlyDictionary<short, OpCode>> Table = new(() =>
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => code.Value));

    /// <summary>
    /// Every constant the method loads, in the order it loads them.
    ///
    /// Empty rather than an error for anything with no body to read: abstract
    /// methods, extern ones, and anything the runtime will not hand over.
    /// </summary>
    public static IEnumerable<object?> Constants(MethodBase method)
    {
        byte[] instructions;
        Module module;

        try
        {
            var body = method.GetMethodBody();
            if (body is null) yield break;

            instructions = body.GetILAsByteArray() ?? [];
            module = method.Module;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A body the runtime declines to produce is not a failure of the
            // run that asked for it.
            yield break;
        }

        var at = 0;

        while (at < instructions.Length)
        {
            var code = instructions[at];
            short number = code;
            at++;

            // Everything above the two-byte prefix is a second table.
            if (code == 0xFE)
            {
                if (at >= instructions.Length) yield break;
                number = (short)(0xFE00 | instructions[at]);
                at++;
            }

            if (!Table.Value.TryGetValue(number, out var operation)) yield break;

            var operand = at;
            var width = Width(operation.OperandType, instructions, operand);

            // Past the end means the stream was not what it claimed to be.
            // Stopping is right: a decoder that guesses its way forward reports
            // constants nobody wrote.
            if (width < 0 || operand + width > instructions.Length) yield break;

            at = operand + width;

            var found = Loaded(operation, instructions, operand, module);
            if (found is not null) yield return found;
        }
    }

    /// <summary>The value this instruction loads, or null if it loads none.</summary>
    private static object? Loaded(OpCode operation, byte[] instructions, int operand, Module module)
    {
        if (operation == OpCodes.Ldc_I4) return BitConverter.ToInt32(instructions, operand);
        if (operation == OpCodes.Ldc_I4_S) return (int)(sbyte)instructions[operand];
        if (operation == OpCodes.Ldc_I8) return BitConverter.ToInt64(instructions, operand);
        if (operation == OpCodes.Ldc_R4) return BitConverter.ToSingle(instructions, operand);
        if (operation == OpCodes.Ldc_R8) return BitConverter.ToDouble(instructions, operand);

        // The short forms. `>= 3` compiles to one of these, so leaving them out
        // would leave out most of the boundaries worth having.
        if (operation == OpCodes.Ldc_I4_M1) return -1;
        if (operation == OpCodes.Ldc_I4_0) return 0;
        if (operation == OpCodes.Ldc_I4_1) return 1;
        if (operation == OpCodes.Ldc_I4_2) return 2;
        if (operation == OpCodes.Ldc_I4_3) return 3;
        if (operation == OpCodes.Ldc_I4_4) return 4;
        if (operation == OpCodes.Ldc_I4_5) return 5;
        if (operation == OpCodes.Ldc_I4_6) return 6;
        if (operation == OpCodes.Ldc_I4_7) return 7;
        if (operation == OpCodes.Ldc_I4_8) return 8;

        if (operation != OpCodes.Ldstr) return null;

        try
        {
            return module.ResolveString(BitConverter.ToInt32(instructions, operand));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A token this module cannot resolve is one string, not a failure.
            return null;
        }
    }

    /// <summary>
    /// How many bytes of operand follow, which is how the next instruction is
    /// found.
    ///
    /// A switch is the only variable one: four bytes saying how many targets
    /// there are, then four bytes for each.
    /// </summary>
    private static int Width(OperandType operand, byte[] instructions, int at) => operand switch
    {
        OperandType.InlineNone => 0,

        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI
            or OperandType.ShortInlineVar => 1,

        OperandType.InlineVar => 2,

        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,

        OperandType.InlineI8 or OperandType.InlineR => 8,

        OperandType.InlineSwitch => at + 4 > instructions.Length
            ? -1
            : 4 + (4 * BitConverter.ToInt32(instructions, at)),

        _ => -1,
    };
}
