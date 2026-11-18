using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace OmegaBotWrapper.Logging
{
    /// <summary>
    /// Best-effort card name lookup used only for readable duel logs.
    /// Names are registered by the host (e.g. from the deck *CardId constant classes);
    /// unknown cards are shown by their ID.
    /// </summary>
    public static class CardNames
    {
        private static readonly ConcurrentDictionary<int, string> _names = new();

        public static void Register(int id, string name)
        {
            if (id > 0 && !string.IsNullOrWhiteSpace(name))
                _names.TryAdd(id, name);
        }

        /// <summary>Sets the name, replacing any earlier one (used for official names from the card database).</summary>
        public static void Set(int id, string name)
        {
            if (id > 0 && !string.IsNullOrWhiteSpace(name))
                _names[id] = name;
        }

        /// <summary>
        /// Registers every public const int on the given type, turning the
        /// PascalCase constant name into a spaced name (BlackwingSiroccoTheDawn -> Blackwing Sirocco The Dawn).
        /// </summary>
        public static void RegisterConstants(Type type)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsLiteral && field.FieldType == typeof(int) && field.GetRawConstantValue() is int id)
                    Register(id, SplitPascalCase(field.Name));
            }
        }

        /// <summary>
        /// Registers constants from every type in the assembly whose name ends with the given suffix (default "CardId").
        /// </summary>
        public static void RegisterConstantsFromAssembly(Assembly assembly, string typeNameSuffix = "CardId")
        {
            foreach (var type in assembly.GetTypes().Where(t => t.Name.EndsWith(typeNameSuffix, StringComparison.Ordinal)))
                RegisterConstants(type);
        }

        /// <summary>
        /// Registers every member of an enum whose values are card IDs (e.g. ThunderKingRaiOh = 71564252).
        /// </summary>
        public static void RegisterEnum(Type enumType)
        {
            if (!enumType.IsEnum)
                return;
            foreach (var value in System.Enum.GetValues(enumType))
            {
                int id = Convert.ToInt32(value);
                Register(id, SplitPascalCase(System.Enum.GetName(enumType, value) ?? ""));
            }
        }

        public static bool TryGet(int id, out string name) => _names.TryGetValue(id, out name!);

        /// <summary>Card name, or "#id" if unknown, or "?" for hidden cards (id 0).</summary>
        public static string Get(int id)
        {
            if (id <= 0)
                return "?";
            return _names.TryGetValue(id, out var name) ? name : $"#{id}";
        }

        /// <summary>Card name with its ID, e.g. "Dark Grepher (14536035)".</summary>
        public static string GetWithId(int id)
        {
            if (id <= 0)
                return "?";
            return _names.TryGetValue(id, out var name) ? $"{name} ({id})" : $"#{id}";
        }

        /// <summary>
        /// Effect descriptions follow the YGOPro convention: cardId * 16 + effect index.
        /// </summary>
        public static string DescribeEffect(int description)
        {
            if (description <= 0)
                return "";
            int cardId = description >> 4;
            int effectIndex = description & 0xF;
            if (cardId <= 0)
                return $"desc {description}";
            return $"effect #{effectIndex} of {Get(cardId)}";
        }

        private static string SplitPascalCase(string value)
        {
            var sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool boundary = i > 0 && (
                    (char.IsUpper(c) && !char.IsUpper(value[i - 1])) ||
                    (char.IsUpper(c) && i + 1 < value.Length && char.IsLower(value[i + 1]) && char.IsUpper(value[i - 1])) ||
                    (char.IsDigit(c) && !char.IsDigit(value[i - 1])));
                if (boundary)
                    sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
