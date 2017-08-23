using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.Collections;

namespace Utilities
{
    class CommandLineSwitchParser
    {
        string _switchPrefix = "/";

        public string SwitchPrefix
        {
            set
            {
                if (string.IsNullOrEmpty(value))
                    throw new InvalidOperationException("SwitchPrefix cannot be null or empty.");

                _switchPrefix = value;
            }
            get { return _switchPrefix; }
        }

        public CommandLineSwitchParser()
        {
        }

        private bool TrySetParse<T>(T output, FieldInfo field, Type type, string value)
        {
            if (type.IsEnum)
            {
                field.SetValue(output, Enum.Parse(type, value));
                return true;
            }
            else 
            {
                MethodInfo parseFunc = null;

                parseFunc = type.GetMethod("Parse", new Type[] { typeof(string)});
                if (parseFunc == null)
                    return false;

                field.SetValue(output, parseFunc.Invoke(output, new object[] { value }));
                return true;
            }
        }

        private void SetValue<T>(T output, FieldInfo field, Type type, string value)
        {
            if (!TrySetParse(output, field, type, value))
            {
                // Hope for the best!
                field.SetValue(output, value);
            }
        }

        delegate T ParseDelegate<T>(string s);

        private IList ConvertStringList(IList<string> strings, Type elementType)
        {
            if (elementType == typeof(string))
                return strings as IList;

            MethodInfo parseFunc = elementType.GetMethod("Parse", new Type[] { typeof(string) });
            IList list = (IList)Array.CreateInstance(elementType, strings.Count);
            if (parseFunc != null)
            {
                for (int i = 0; i < strings.Count; ++i)
                {
                    list[i] = parseFunc.Invoke(null, new object[] { strings[i] });
                }
            }
            else
            {
                for (int i = 0; i < strings.Count; ++i)
                {
                    list[i] = Activator.CreateInstance(elementType, strings[i]); ;
                }
            }
            return list;
        }

        private bool HasEnumerableConstructor(Type type)
        {
            return 
                type != typeof(string) && 
                type.GetInterfaces().Any(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        }

        private void SetValues<T>(T output, FieldInfo field, Type elementType, IList<string> values)
        {
            IList objs = ConvertStringList(values, elementType);
            if (field.FieldType.IsArray)
            {
                string[] arr = new string[objs.Count];
                objs.CopyTo(arr, 0);

                // If the array already exists, extend it
                string[] existing = field.GetValue(output) as string[];
                if (existing != null && existing.Length > 0)
                {
                    string[] newArr = new string[arr.Length + existing.Length];
                    existing.CopyTo(newArr, 0);
                    arr.CopyTo(newArr, existing.Length);
                    field.SetValue(output, newArr);
                }
                else
                {
                    field.SetValue(output, arr);
                }
            }
            else
            {
                IList existing = field.GetValue(output) as IList;
                if (existing != null && existing.Count > 0)
                {
                    IList newList = (IList)Array.CreateInstance(elementType, existing.Count + objs.Count);
                    for (int i = 0; i < existing.Count; ++i)
                        newList[i] = existing[i];
                    for (int i = 0; i < objs.Count; ++i)
                        newList[i + existing.Count] = objs[i];
                    field.SetValue(output, Activator.CreateInstance(field.FieldType, newList));
                }
                else
                {
                    field.SetValue(output, Activator.CreateInstance(field.FieldType, objs));
                }
            }
        }

        public void Parse<T>(string[] args, T output) where T : class
        {
            Type type = typeof(T);

            // Grab a list of properties from T
            Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                fields.Add(field.Name, field);
            }

            for (int i = 0; i < args.Length; ++i)
            {
                if (!args[i].StartsWith(SwitchPrefix))
                {
                    throw new Exception("Invalid switch: " + args[i]);
                }

                string arg = args[i].Substring(1);

                // Find the property in T
                if (!fields.ContainsKey(arg))
                {
                    throw new Exception("Unknown switch: " + arg);
                }

                FieldInfo field = fields[arg];

                // If it's a toggle no need to check for more args
                if (field.FieldType == typeof(bool) || field.FieldType == typeof(Nullable<bool>))
                {
                    field.SetValue(output, true);
                }
                else if (i < args.Length - 1)
                {
                    if (args[i + 1].StartsWith(SwitchPrefix))
                        throw new Exception("Expected: value for " + arg);

                    if (field.FieldType.IsArray || HasEnumerableConstructor(field.FieldType))
                    {
                        List<string> values = new List<string>();
                        while (i < args.Length - 1 && !args[i+1].StartsWith(SwitchPrefix))
                        {
                            values.Add(args[++i]);
                        }

                        if (field.FieldType.IsArray)
                            SetValues(output, field, field.FieldType.GetElementType(), values);
                        else
                            SetValues(output, field, field.FieldType.GetGenericArguments()[0], values);
                    }
                    else
                    {
                        SetValue(output, field, field.FieldType, args[++i]);
                    }
                }
            }
        }
    }
}
