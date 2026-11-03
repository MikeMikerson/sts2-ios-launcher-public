using Mono.Cecil.Cil;

namespace STS2Mobile.GamePatcher;

internal static class InstructionEdits
{
    public static void Set(this Instruction instruction, OpCode opCode, object operand)
    {
        instruction.OpCode = opCode;
        instruction.Operand = operand;
    }

    public static VariableDefinition GetLocal(this Instruction load, MethodBody body) => load.OpCode.Code switch
    {
        Code.Ldloc_0 => body.Variables[0],
        Code.Ldloc_1 => body.Variables[1],
        Code.Ldloc_2 => body.Variables[2],
        Code.Ldloc_3 => body.Variables[3],
        _ => (VariableDefinition)load.Operand
    };

    public static bool IsLoad(this Instruction instruction, VariableDefinition local, MethodBody body) =>
        instruction.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3 && instruction.GetLocal(body) == local;
}
