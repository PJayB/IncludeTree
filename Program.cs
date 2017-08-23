using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.IO;
using System.Diagnostics;

namespace IncludeTree
{
    class Program
    {
        class SearchPaths
        {
            HashSet<string> _includePaths = new HashSet<string>();

            public bool AddSearchPath(string path)
            {
                if (string.IsNullOrEmpty(path))
                    return false;

                if (!Path.IsPathRooted(path))
                    path = Path.GetFullPath(path);

                if (Directory.Exists(path) && !_includePaths.Contains(path))
                {
                    _includePaths.Add(path);
                    return true;
                }

                return false;
            }

            public bool Resolve(string path, out string fullPath)
            {
                fullPath = string.Empty;
                if (string.IsNullOrEmpty(path))
                    return false;

                string sanitizedPath = path.Replace('/', '\\');

                fullPath = sanitizedPath;
                if (Path.IsPathRooted(fullPath))
                    return File.Exists(fullPath);

                foreach (var ipath in _includePaths)
                {
                    fullPath = Path.Combine(ipath, sanitizedPath);
                    if (File.Exists(fullPath))
                        return true;
                }

                // Restore to original and abort
                fullPath = path;
                return false;
            }
        }

        class Node
        {
            public struct Reference
            {
                public Node Child;
                public int LineNumber;

                public Reference(Node c, int l)
                {
                    Child = c;
                    LineNumber = l;
                }
            }

            List<Reference> _refs = new List<Reference>();

            public Node(string path)
            {
                FilePath = path;
            }

            public bool IsRealFile { get { return File.Exists(FilePath); } }
            public bool HasChildren { get { return _refs.Count > 0; } }

            public string FilePath;
            public IEnumerable<Reference> References { get { return _refs; } }

            public void AddChild(Node child, int lineNumber)
            {
                foreach (var reference in _refs)
                {
                    if (reference.Child.FilePath == child.FilePath)
                        return;
                }
                _refs.Add(new Reference(child, lineNumber));
            }
        }

        class UniqueNodeList
        {
            Dictionary<string, Node> _uniqueNodes = new Dictionary<string, Node>();
            SearchPaths _searchPaths;
            Regex _searchPattern;

            public UniqueNodeList(SearchPaths searchPaths, Regex searchPattern)
            {
                _searchPaths = searchPaths;
                _searchPattern = searchPattern;
            }

            public Node GetNodeForPath(string path)
            {
                if (_uniqueNodes.ContainsKey(path))
                    return _uniqueNodes[path];
                else
                    return AddNode(new Node(path));
            }

            private Node AddNode(Node node)
            {
                Debug.Assert(!_uniqueNodes.ContainsKey(node.FilePath));
                _uniqueNodes.Add(node.FilePath, node);
                BuildHierarchy(node);
                return node;
            }

            public Node[] ToArray()
            {
                Node[] arr = new Node[_uniqueNodes.Count];
                int c = 0;
                foreach (var i in _uniqueNodes)
                {
                    arr[c++] = i.Value;
                }
                return arr;
            }

            public void BuildHierarchy(Node node)
            {
                if (node.IsRealFile)
                {
                    try
                    {
                        using (StreamReader r = new StreamReader(node.FilePath))
                        {
                            for (int lineNum = 1; !r.EndOfStream; lineNum++)
                            {
                                string line = r.ReadLine();

                                Match match = _searchPattern.Match(line);
                                if (match != null && match.Groups.Count > 1)
                                {
                                    // Resolve the path
                                    string resolvedPath;
                                    _searchPaths.Resolve(match.Groups[1].Value, out resolvedPath);
                                    Node child = GetNodeForPath(resolvedPath);
                                    node.AddChild(child, lineNum);
                                }
                            }
                        }
                    }
                    catch (FileNotFoundException)
                    {
                    }
                }
            }
        }

        static string Tabulate(string content, int tabLevel)
        {
            StringBuilder str = new StringBuilder();
            while (tabLevel-- > 0)
            {
                str.Append("| ");
            }
            str.Append(content);
            return str.ToString();
        }

        static void PrintNode(Node node, int tabLevel, HashSet<string> visited)
        {
            foreach (var reference in node.References)
            {
                if (visited.Contains(reference.Child.FilePath) && reference.Child.HasChildren)
                {
                    Console.WriteLine(Tabulate($"[{reference.LineNumber}]: {reference.Child.FilePath} (see above)", tabLevel));
                }
                else
                {
                    visited.Add(reference.Child.FilePath);
                    if (reference.Child.IsRealFile)
                    {
                        Console.WriteLine(Tabulate($"[{reference.LineNumber}]: {reference.Child.FilePath}", tabLevel));
                        PrintNode(reference.Child, tabLevel + 1, visited);
                    }
                    else
                    {
                        Console.WriteLine(Tabulate($"[{reference.LineNumber}]: {reference.Child.FilePath} (unresolved)", tabLevel));
                    }
                }
            }
        }

        static void SetupDefaultIncludePaths(SearchPaths searchPaths)
        {
            searchPaths.AddSearchPath(Environment.CurrentDirectory);

            string[] includePaths = (Environment.GetEnvironmentVariable("INCLUDE") ?? string.Empty).Split(';');
            foreach (string includePath in includePaths)
            {
                if (!searchPaths.AddSearchPath(includePath))
                    Console.WriteLine($"Couldn't find '{includePath}' specified in %INCLUDE%");
            }
        }

        public class CommandLineOptions
        {
            public string[] I = new string[0];
        };

        static void Main(string[] args)
        {
            SearchPaths searchPaths = new SearchPaths();

            SetupDefaultIncludePaths(searchPaths);

            CommandLineOptions options = new CommandLineOptions();
            Utilities.CommandLineSwitchParser commandLineParser = new Utilities.CommandLineSwitchParser();

            // todo: catch exceptions
            commandLineParser.Parse(args, options);

            // Parse command line arguments
            foreach (var path in options.I)
            {
                if (!searchPaths.AddSearchPath(path))
                    Console.WriteLine($"Couldn't add include directory '{path}'");
            }

            // Find all cpp and header files
            Console.WriteLine("Finding source files...");
            IEnumerable<string> cppFilesToSearch = Directory.EnumerateFiles(Environment.CurrentDirectory, "*.cpp");
            IEnumerable<string> hFilesToSearch = Directory.EnumerateFiles(Environment.CurrentDirectory, "*.h");

            List<string> allFiles = new List<string>();
            allFiles.AddRange(cppFilesToSearch);
            allFiles.AddRange(hFilesToSearch);

            Console.WriteLine($"{allFiles.Count} files found.");

            Regex includeRegex = new Regex(@"^\s*#include\s+[""<]([^""<>]+?)["">]", RegexOptions.Singleline | RegexOptions.Compiled);

            UniqueNodeList uniqueNodes = new UniqueNodeList(searchPaths, includeRegex);

            // Read each file
            HashSet<string> visited = new HashSet<string>();
            foreach (var file in allFiles)
            {
                Node node = uniqueNodes.GetNodeForPath(file);
                uniqueNodes.BuildHierarchy(node);

                Console.WriteLine(Tabulate(node.FilePath, 0));
                PrintNode(node, 1, visited);
            }
        }
    }
}
