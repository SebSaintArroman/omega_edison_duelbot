namespace DuelSim.Data;

/// <summary>
/// Evaluates "announce card" filters (the RPN opcode lists behind MSG_ANNOUNCE_CARD and Omega's /card-list), in
/// Omega's classic 32-bit form: opcodes are 0x40000000 + op, anything else is a literal value.
/// </summary>
public static class CardFilter
{
    private const int Add = 0x40000000, Sub = 0x40000001, Mul = 0x40000002, Div = 0x40000003, And = 0x40000004,
        Or = 0x40000005, Neg = 0x40000006, Not = 0x40000007, BAnd = 0x40000008, BOr = 0x40000009, BNot = 0x40000010,
        BXor = 0x40000011, LShift = 0x40000012, RShift = 0x40000013, AllowAliases = 0x40000014, AllowTokens = 0x40000015,
        IsCode = 0x40000100, IsSetCard = 0x40000101, IsType = 0x40000102, IsRace = 0x40000103, IsAttribute = 0x40000104,
        GetCode = 0x40000105, GetSetCard = 0x40000106, GetRace = 0x40000108, GetAttribute = 0x40000109;
    // Named like the opcode (Omega: GET_TYPE); hides object.GetType on purpose.
    private new const int GetType = 0x40000107;

    /// <summary>EDOPro writes 64-bit opcodes (op in the high half); Omega's API uses the high half as a 32-bit value.</summary>
    public static int ToOmega(ulong value) => (value & 0x4000000000000000UL) != 0 ? (int)(value >> 32) : (int)value;

    public static bool Matches(IReadOnlyList<int> filters, CardRecord card)
    {
        if (filters.Count == 0)
            return true;
        var stack = new Stack<long>();
        bool aliases = false, tokens = false;
        long Pop() => stack.Count > 0 ? stack.Pop() : 0;
        foreach (int op in filters)
        {
            switch (op)
            {
                case Add: { var b = Pop(); var a = Pop(); stack.Push(a + b); break; }
                case Sub: { var b = Pop(); var a = Pop(); stack.Push(a - b); break; }
                case Mul: { var b = Pop(); var a = Pop(); stack.Push(a * b); break; }
                case Div: { var b = Pop(); var a = Pop(); stack.Push(b == 0 ? 0 : a / b); break; }
                case And: { var b = Pop(); var a = Pop(); stack.Push(a != 0 && b != 0 ? 1 : 0); break; }
                case Or: { var b = Pop(); var a = Pop(); stack.Push(a != 0 || b != 0 ? 1 : 0); break; }
                case Neg: stack.Push(-Pop()); break;
                case Not: stack.Push(Pop() == 0 ? 1 : 0); break;
                case BAnd: { var b = Pop(); var a = Pop(); stack.Push(a & b); break; }
                case BOr: { var b = Pop(); var a = Pop(); stack.Push(a | b); break; }
                case BNot: stack.Push(~Pop()); break;
                case BXor: { var b = Pop(); var a = Pop(); stack.Push(a ^ b); break; }
                case LShift: { var b = Pop(); var a = Pop(); stack.Push(a << (int)b); break; }
                case RShift: { var b = Pop(); var a = Pop(); stack.Push(a >> (int)b); break; }
                case AllowAliases: aliases = true; break;
                case AllowTokens: tokens = true; break;
                case IsCode: { var code = Pop(); stack.Push(card.Id == code || card.Alias == code ? 1 : 0); break; }
                case IsSetCard: { var set = Pop(); stack.Push(IsSet(card, set) ? 1 : 0); break; }
                case IsType: stack.Push((card.Type & Pop()) != 0 ? 1 : 0); break;
                case IsRace: stack.Push((card.Race & Pop()) != 0 ? 1 : 0); break;
                case IsAttribute: stack.Push((card.Attribute & Pop()) != 0 ? 1 : 0); break;
                case GetCode: stack.Push(card.Id); break;
                case GetSetCard: stack.Push(card.SetCodes().FirstOrDefault()); break;
                case GetType: stack.Push(card.Type); break;
                case GetRace: stack.Push(card.Race); break;
                case GetAttribute: stack.Push(card.Attribute); break;
                default: stack.Push(op); break;
            }
        }
        if (!tokens && (card.Type & 0x4000) != 0)
            return false;
        if (!aliases && card.Alias != 0 && Math.Abs(card.Id - card.Alias) < 20 && !filters.Contains(IsCode))
            return false;
        return stack.Count == 1 && stack.Peek() != 0;
    }

    private static bool IsSet(CardRecord card, long set)
    {
        long setType = set & 0xfff, setSub = set & 0xf000;
        foreach (var code in card.SetCodes())
        {
            if ((code & 0xfff) == setType && (code & 0xf000 & setSub) == setSub)
                return true;
        }
        return false;
    }
}
