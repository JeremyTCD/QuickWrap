using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Xml;

namespace Jering.Quickwrap
{
    class Program
    {
        static readonly AdhocWorkspace _workspace = new AdhocWorkspace();
        static readonly SyntaxGenerator _syntaxGenerator = SyntaxGenerator.GetGenerator(_workspace, LanguageNames.CSharp);
        static readonly Dictionary<string, string> _keywords = new Dictionary<string, string>()
        {
            { "Void", "void" }
        };

        static void Main(string[] args)
        {
            string outputNamespace = "Jering.IocServices.System.Net.Http";
            Type httpClientType = typeof(HttpClient);

            HttpClient test = new HttpClient();

            // Get public methods and properties
            List<MethodInfo> methodInfos = GetMethodInfos(httpClientType);
            List<PropertyInfo> propertyInfos = GetPropertyInfos(httpClientType);

            // Get namespaceNames used in public method and property declarations
            HashSet<string> namespaceNames = GetNamesOfNamespacesUsed(methodInfos, propertyInfos);

            // Include namespace of current type
            // TODO remove redundant namespaces?
            namespaceNames.Add(httpClientType.Namespace);

            // Create interface and class
            XmlDocument documentation = GetDocumentation(httpClientType);
            string interfaceAsString = CreateInterface(outputNamespace, httpClientType, namespaceNames, methodInfos, propertyInfos);
            string classAsString = CreateClass(outputNamespace, httpClientType, namespaceNames, methodInfos, propertyInfos);
        }

        static XmlDocument GetDocumentation(Type type)
        {
            Assembly assembly = type.GetTypeInfo().Assembly;
            string dllPath = type.GetTypeInfo().Assembly.Location;
            string documentationPath = dllPath.Substring(0, dllPath.LastIndexOf('.') + 1) + "xml";

            // System.* assemblies are loaded from "Program Files (x86)/dotnet/shared/Microsoft.NETCore.App/<version>" instead of from
            // Nuget packages, so their comments aren't available.
            if (!File.Exists(documentationPath))
            {
                return null;
            }

            var xmlDocument = new XmlDocument();
            using (FileStream fileStream = File.OpenRead(documentationPath))
            {
                xmlDocument.Load(fileStream);
            }

            return xmlDocument;
        }

        static string CreateClass(string outputNamespace, Type type, HashSet<string> namespaceNames, List<MethodInfo> methodInfos, List<PropertyInfo> propertyInfos)
        {
            // Properties
            IEnumerable<SyntaxNode> memberDeclarations = propertyInfos.Select(CreateProperty);

            // Methods
            memberDeclarations = memberDeclarations.Concat(methodInfos.Select(CreateMethod));

            // If there are any non-static methods, create an instance of the type
            if (methodInfos.Any(methodInfo => !methodInfo.IsStatic))
            {
                SyntaxNode typeSyntax = CreateTypeSyntax(type);

                // Field
                string typeName = type.Name;
                string fieldName = $"_{char.ToLowerInvariant(typeName[0])}{typeName.Substring(1)}";
                SyntaxNode fieldDeclaration = _syntaxGenerator.FieldDeclaration(fieldName, typeSyntax, Accessibility.Private);

                // Constructor
                SyntaxNode objectCreationExpression = _syntaxGenerator.ObjectCreationExpression(typeSyntax);
                // _syntaxGenerator.AssignmentStatement parenthesizes the right operand - https://github.com/dotnet/roslyn/blob/master/src/Workspaces/CSharp/Portable/CodeGeneration/CSharpSyntaxGenerator.cs#L3794
                SyntaxNode simpleAssignmentExpression = SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, (ExpressionSyntax)_syntaxGenerator.IdentifierName(fieldName), (ExpressionSyntax)objectCreationExpression);
                SyntaxNode expressionStatement = _syntaxGenerator.ExpressionStatement(simpleAssignmentExpression);
                SyntaxNode constructorDeclaration = _syntaxGenerator.ConstructorDeclaration(typeName, accessibility: Accessibility.Public, statements: new SyntaxNode[] { expressionStatement });

                // Add declarations to class
                memberDeclarations = memberDeclarations.Prepend(constructorDeclaration);
                memberDeclarations = memberDeclarations.Prepend(fieldDeclaration);
            }

            // Class
            SyntaxNode classDeclaration = _syntaxGenerator.ClassDeclaration($"{type.Name}Service", accessibility: Accessibility.Public, interfaceTypes: new SyntaxNode[] { SyntaxFactory.ParseTypeName($"I{type.Name}Service") },
                members: memberDeclarations);

            // Namespace
            NamespaceDeclarationSyntax namespaceDeclaration = (NamespaceDeclarationSyntax)_syntaxGenerator.NamespaceDeclaration(outputNamespace, classDeclaration);

            // Usings
            IEnumerable<SyntaxNode> usingDirectives = namespaceNames.Select(namespaceName => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(namespaceName)));

            // Compilation unit
            SyntaxNode compilationUnit = _syntaxGenerator.CompilationUnit(usingDirectives.Append(namespaceDeclaration));

            // Format
            SyntaxNode formattedNode = Formatter.Format(compilationUnit, new AdhocWorkspace());
            var stringBuilder = new StringBuilder();
            using (var writer = new StringWriter(stringBuilder))
            {
                formattedNode.WriteTo(writer);
            }

            return stringBuilder.ToString();
        }

        static SyntaxNode CreateProperty(PropertyInfo propertyInfo)
        {
            string declaringTypeName = propertyInfo.DeclaringType.Name;
            string fieldName = $"_{char.ToLowerInvariant(declaringTypeName[0])}{declaringTypeName.Substring(1)}";

            SyntaxNode getStatement = null;
            SyntaxNode setStatement = null;
            DeclarationModifiers declarationModifiers = default(DeclarationModifiers);
            // Property is public so it CanWrite and CanRead can't both be true
            if (!propertyInfo.CanWrite)
            {
                declarationModifiers = DeclarationModifiers.ReadOnly;
            }
            else if (!propertyInfo.CanRead)
            {
                declarationModifiers = DeclarationModifiers.WriteOnly;
            }

            if (propertyInfo.CanRead)
            {
                SyntaxNode memberAccessExpression = _syntaxGenerator.MemberAccessExpression(_syntaxGenerator.IdentifierName(fieldName), propertyInfo.Name);
                SyntaxNode returnStatement = _syntaxGenerator.ReturnStatement(memberAccessExpression);
                getStatement = returnStatement;
            }

            if (propertyInfo.CanWrite)
            {
                SyntaxNode memberAccessExpression = _syntaxGenerator.MemberAccessExpression(_syntaxGenerator.IdentifierName(fieldName), propertyInfo.Name);
                SyntaxNode simpleAssignmentExpression = SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, (ExpressionSyntax)memberAccessExpression, (ExpressionSyntax)_syntaxGenerator.IdentifierName("value"));
                setStatement = simpleAssignmentExpression;
            }

            // TODO create manually, use lambda staments for getters and setters
            return _syntaxGenerator.PropertyDeclaration(propertyInfo.Name,
                CreateTypeSyntax(propertyInfo.PropertyType),
                Accessibility.Public,
                declarationModifiers,
                setAccessorStatements: setStatement == null ? null : new SyntaxNode[] { setStatement },
                getAccessorStatements: getStatement == null ? null : new SyntaxNode[] { getStatement });
        }

        static SyntaxNode CreateMethod(MethodInfo methodInfo)
        {
            SyntaxNode returnType = CreateTypeSyntax(methodInfo.ReturnType);

            var arguments = new List<SyntaxNode>();
            foreach (ParameterInfo parameterInfo in methodInfo.GetParameters())
            {
                arguments.Add(_syntaxGenerator.Argument(_syntaxGenerator.IdentifierName(parameterInfo.Name)));
            }

            SyntaxNode statement = null;
            if (methodInfo.IsStatic)
            {
                // TODO call static method
            }
            else
            {
                string declaringTypeName = methodInfo.DeclaringType.Name;
                string fieldName = $"_{char.ToLowerInvariant(declaringTypeName[0])}{declaringTypeName.Substring(1)}";
                SyntaxNode memberAccessExpression = _syntaxGenerator.MemberAccessExpression(_syntaxGenerator.IdentifierName(fieldName), methodInfo.Name);
                SyntaxNode invocationExpression = _syntaxGenerator.InvocationExpression(memberAccessExpression, arguments);

                if (methodInfo.ReturnType == typeof(void))
                {
                    statement = invocationExpression;
                }
                else
                {
                    statement = _syntaxGenerator.ReturnStatement(invocationExpression);
                }
            }

            var parameters = new List<SyntaxNode>();
            foreach (ParameterInfo parameterInfo in methodInfo.GetParameters())
            {
                // TODO initializer (default values)
                // TODO type parameters
                parameters.Add(_syntaxGenerator.ParameterDeclaration(parameterInfo.Name, CreateTypeSyntax(parameterInfo.ParameterType)));
            }

            return _syntaxGenerator.MethodDeclaration(methodInfo.Name, parameters, returnType: returnType, accessibility: Accessibility.Public, statements: new SyntaxNode[] { statement });
        }

        static string CreateInterface(string outputNamespace, Type type, HashSet<string> namespaceNames, List<MethodInfo> methodInfos, List<PropertyInfo> propertyInfos)
        {
            // Properties
            IEnumerable<SyntaxNode> propertyDeclarations = propertyInfos.Select(CreatePropertyDeclaration);

            // Methods
            IEnumerable<SyntaxNode> methodDeclarations = methodInfos.Select(CreateMethodDeclaration);

            // Class
            SyntaxNode interfaceDeclaration = _syntaxGenerator.InterfaceDeclaration($"I{type.Name}Service", accessibility: Accessibility.Public, members: propertyDeclarations.Concat(methodDeclarations));

            // Namespace
            NamespaceDeclarationSyntax namespaceDeclaration = (NamespaceDeclarationSyntax)_syntaxGenerator.NamespaceDeclaration(outputNamespace, interfaceDeclaration);

            // Usings
            IEnumerable<SyntaxNode> usingDirectives = namespaceNames.Select(namespaceName => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(namespaceName)));

            // Compilation unit
            SyntaxNode compilationUnit = _syntaxGenerator.CompilationUnit(usingDirectives.Append(namespaceDeclaration));

            // Format
            SyntaxNode formattedNode = Formatter.Format(compilationUnit, new AdhocWorkspace());
            var stringBuilder = new StringBuilder();
            using (var writer = new StringWriter(stringBuilder))
            {
                formattedNode.WriteTo(writer);
            }

            return stringBuilder.ToString();
        }

        static SyntaxNode CreatePropertyDeclaration(PropertyInfo propertyInfo)
        {
            DeclarationModifiers declarationModifiers = default(DeclarationModifiers);
            // Property is public so it CanWrite and CanRead can't both be true
            if (!propertyInfo.CanWrite)
            {
                declarationModifiers = DeclarationModifiers.ReadOnly;
            }
            else if (!propertyInfo.CanRead)
            {
                declarationModifiers = DeclarationModifiers.WriteOnly;
            }

            return _syntaxGenerator.PropertyDeclaration(propertyInfo.Name, CreateTypeSyntax(propertyInfo.PropertyType), modifiers: declarationModifiers);
        }

        static SyntaxNode CreateMethodDeclaration(MethodInfo methodInfo)
        {
            SyntaxNode returnType = CreateTypeSyntax(methodInfo.ReturnType);

            var parameters = new List<SyntaxNode>();
            foreach (ParameterInfo parameterInfo in methodInfo.GetParameters())
            {
                // TODO initializer (default values)
                // TODO type parameters
                parameters.Add(_syntaxGenerator.ParameterDeclaration(parameterInfo.Name, CreateTypeSyntax(parameterInfo.ParameterType)));
            }

            return _syntaxGenerator.MethodDeclaration(methodInfo.Name, parameters, returnType: returnType);
        }

        /// <summary>
        /// Creates a TypeSyntax, recursively creating generic argument types.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        static SyntaxNode CreateTypeSyntax(Type type)
        {
            // TODO safe?
            string typeName = type.Name;
            int tildeIndex = typeName.IndexOf('`');
            bool hasGenericTypeArguments = tildeIndex != -1;

            if (hasGenericTypeArguments)
            {
                typeName = type.Name.Substring(0, tildeIndex);
            }

            if (_keywords.TryGetValue(typeName, out string keyword))
            {
                typeName = keyword;
            }

            if (hasGenericTypeArguments)
            {
                return _syntaxGenerator.GenericName(typeName, type.GenericTypeArguments.Select(CreateTypeSyntax));
            }

            return SyntaxFactory.ParseTypeName(typeName);
        }

        static List<MethodInfo> GetMethodInfos(Type type)
        {
            List<MethodInfo> result = new List<MethodInfo>();

            // Add method infos
            MethodInfo[] publicMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (MethodInfo publicMethod in publicMethods)
            {
                // Filter our property getters and setters, indexers, event accessors and operator overloads
                if (publicMethod.IsSpecialName)
                {
                    continue;
                }

                result.Add(publicMethod);
            }

            return result;
        }

        static List<PropertyInfo> GetPropertyInfos(Type type)
        {
            List<PropertyInfo> result = new List<PropertyInfo>();

            // Add property infos
            PropertyInfo[] publicProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (PropertyInfo publicProperty in publicProperties)
            {
                // TODO what kinds of properties have special names?
                if (publicProperty.IsSpecialName)
                {
                    continue;
                }

                result.Add(publicProperty);
            }

            return result;
        }

        static HashSet<string> GetNamesOfNamespacesUsed(IEnumerable<MethodInfo> methodInfos, IEnumerable<PropertyInfo> propertyInfos)
        {
            HashSet<string> result = new HashSet<string>();

            foreach (MethodInfo methodInfo in methodInfos)
            {
                // Add namespaceNames for return type
                AddnamespaceNamesUsed(result, methodInfo.ReturnType);

                // Add namespaceNames for parameter types
                foreach (ParameterInfo parameter in methodInfo.GetParameters())
                {
                    AddnamespaceNamesUsed(result, parameter.ParameterType);
                }
            }

            foreach (PropertyInfo propertyInfo in propertyInfos)
            {
                // Add namespaceNames for property type
                AddnamespaceNamesUsed(result, propertyInfo.PropertyType);
            }

            return result;
        }

        /// <summary>
        /// Extracts namespaceNames used by type and any generic arguments recursively
        /// </summary>
        /// <param name="result"></param>
        /// <param name="type"></param>
        static void AddnamespaceNamesUsed(HashSet<string> result, Type type)
        {
            result.Add(type.Namespace);

            foreach (Type genericArgumentType in type.GetGenericArguments())
            {
                AddnamespaceNamesUsed(result, genericArgumentType);
            }
        }
    }
}
