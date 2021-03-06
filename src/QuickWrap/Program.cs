﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            string outputNamespace = "Jering.IocServices.Newtonsoft.Json";
            Type type = typeof(JsonSerializer);

            // Get public methods and properties
            List<MethodInfo> methodInfos = GetMethodInfos(type);
            List<PropertyInfo> propertyInfos = GetPropertyInfos(type);
            List<EventInfo> eventInfos = GetEventInfos(type);

            // Get namespaceNames used in public method and property declarations
            HashSet<string> namespaceNames = GetNamesOfNamespacesUsed(methodInfos, propertyInfos, eventInfos);

            // Include namespace of current type
            // TODO remove redundant namespaces?
            namespaceNames.Add(type.Namespace);

            // Create interface and class
            XmlDocument documentation = GetDocumentation(type);
            string interfaceAsString = CreateInterface(outputNamespace, type, namespaceNames, methodInfos, propertyInfos, eventInfos);
            string classAsString = CreateClass(outputNamespace, type, namespaceNames, methodInfos, propertyInfos, eventInfos);
        }

        static XmlDocument GetDocumentation(Type type)
        {
            Assembly assembly = type.GetTypeInfo().Assembly;
            string dllPath = type.GetTypeInfo().Assembly.Location;
            string documentationPath = dllPath.Substring(0, dllPath.LastIndexOf('.') + 1) + "xml";

            // System.* assemblies are loaded from "Program Files (x86)/dotnet/shared/Microsoft.NETCore.App/<version>" instead of from
            // Nuget packages, so their comments aren't available.

            // If this reflection based method is going to work, it will be necessary to dig through the whole system around Microsoft.NetCore.App.
            // Documentation on how it all works is patchy at best.
            // - How are assemblies in the metapackage related to those in out of bound packages? How are the assemblies versioned?
            //   - The trail of dependencies for Microsoft.NETCore.App runs cold at Microsoft.NETCore.Platforms, there is no indication of dependencies on the standalone packages (for netcoreapp2.1).
            // - Can assemblies be included directly from the metapackage instead of from dotnet/shared?
            //   - Will have to look through how msbuild is resolving packages, can't find any information on this.
            // - What is their rationale for changing things from project.json times when you simply referenced the metapackages?
            // Misc links:
            // https://gist.github.com/dsplaisted/83d67bbcff9ec1d0aff1bea1bf4ad79a
            // https://github.com/dsplaisted/ReferenceAssemblyPackages
            // https://github.com/dotnet/core/blob/master/release-notes/1.0/sdk/1.0-rc3-implicit-package-refs.md
            //
            // It might be too much of a mess, in which case it would be better to just clone the whole repo and generate ioc wrappers from the source code.

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

        static string CreateClass(string outputNamespace, Type type, HashSet<string> namespaceNames, List<MethodInfo> methodInfos, List<PropertyInfo> propertyInfos, List<EventInfo> eventInfos)
        {
            SyntaxNode underlyingTypeSyntax = CreateTypeSyntax(type);
            string underlyingTypeName = type.Name;
            string underlyingTypeFieldName = $"_{char.ToLowerInvariant(underlyingTypeName[0])}{underlyingTypeName.Substring(1)}";

            // Events and methods may require statements in the constructor
            List<SyntaxNode> constructorStatements = new List<SyntaxNode>();

            // Events
            IEnumerable<SyntaxNode> memberDeclarations = eventInfos.Select(CreateEvent);

            // Properties
            memberDeclarations = memberDeclarations.Concat(propertyInfos.Select(CreateProperty));

            // Methods
            memberDeclarations = memberDeclarations.Concat(methodInfos.Select(CreateMethod));

            // If there are any non-static methods or events, create an instance of the type
            if (methodInfos.Any(methodInfo => !methodInfo.IsStatic) || eventInfos.Count > 0)
            {
                // Initialize default instance in constructor
                SyntaxNode objectCreationExpression = _syntaxGenerator.ObjectCreationExpression(underlyingTypeSyntax);
                // _syntaxGenerator.AssignmentStatement parenthesizes the right operand - https://github.com/dotnet/roslyn/blob/master/src/Workspaces/CSharp/Portable/CodeGeneration/CSharpSyntaxGenerator.cs#L3794
                SyntaxNode simpleAssignmentExpression = SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, (ExpressionSyntax)_syntaxGenerator.IdentifierName(underlyingTypeFieldName), (ExpressionSyntax)objectCreationExpression);
                SyntaxNode expressionStatement = _syntaxGenerator.ExpressionStatement(simpleAssignmentExpression);

                constructorStatements.Add(expressionStatement);
            }

            // Register anon methods for delegates
            if (eventInfos.Count > 0)
            {
                // Register delegates
                foreach (EventInfo eventInfo in eventInfos)
                {
                    // We need the invoke method of the delegate type to figure out its parameters
                    MethodInfo invokeMethod = eventInfo.EventHandlerType.GetMethod("Invoke");
                    ParameterInfo[] parameterInfos = invokeMethod.GetParameters();

                    // Args for invoking the delegate
                    SyntaxNode[] invocationArgs = new SyntaxNode[parameterInfos.Length];
                    for (int i = 0; i < invocationArgs.Length; i++)
                    {
                        invocationArgs[i] = _syntaxGenerator.IdentifierName(parameterInfos[i].Name);
                    }

                    // Delegate invocation
                    SyntaxNode memberBindingExpression = SyntaxFactory.MemberBindingExpression((SimpleNameSyntax)_syntaxGenerator.IdentifierName("Invoke"));
                    SyntaxNode conditionalAccessExpression = SyntaxFactory.ConditionalAccessExpression(
                        (ExpressionSyntax)_syntaxGenerator.IdentifierName(eventInfo.Name),
                        (ExpressionSyntax)_syntaxGenerator.InvocationExpression(memberBindingExpression, invocationArgs)
                    );

                    // Anonymous method that invokes the delegate
                    SyntaxNode[] anonMethodParams = new SyntaxNode[parameterInfos.Length];
                    for (int i = 0; i < invocationArgs.Length; i++)
                    {
                        anonMethodParams[i] = _syntaxGenerator.ParameterDeclaration(parameterInfos[i].Name, CreateTypeSyntax(parameterInfos[i].ParameterType));
                    }
                    SyntaxNode parenthesizedLamdaExpression = SyntaxFactory.ParenthesizedLambdaExpression(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(anonMethodParams)), (CSharpSyntaxNode)conditionalAccessExpression);

                    // Compound assignment for delegate
                    SyntaxNode memberAccessExpression = _syntaxGenerator.MemberAccessExpression(_syntaxGenerator.IdentifierName(underlyingTypeFieldName), _syntaxGenerator.IdentifierName(eventInfo.Name));
                    SyntaxNode addAnonMethodToDelegateExpression = SyntaxFactory.AssignmentExpression(SyntaxKind.AddAssignmentExpression, (ExpressionSyntax)memberAccessExpression, (ExpressionSyntax)parenthesizedLamdaExpression);

                    constructorStatements.Add(addAnonMethodToDelegateExpression);
                }
            }

            // Constructor and default instance
            if (constructorStatements.Count > 0)
            {
                // Default instance field
                SyntaxNode fieldDeclaration = _syntaxGenerator.FieldDeclaration(underlyingTypeFieldName, underlyingTypeSyntax, Accessibility.Private);

                // Constructor
                SyntaxNode constructorDeclaration = _syntaxGenerator.ConstructorDeclaration(underlyingTypeName, accessibility: Accessibility.Public, statements: constructorStatements);

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

        // TODO async methods, default arg values
        static SyntaxNode CreateMethod(MethodInfo methodInfo)
        {
            SyntaxNode returnType = CreateTypeSyntax(methodInfo.ReturnType);

            var arguments = new List<SyntaxNode>();
            foreach (ParameterInfo parameterInfo in methodInfo.GetParameters())
            {
                arguments.Add(_syntaxGenerator.Argument(_syntaxGenerator.IdentifierName(parameterInfo.Name)));
            }

            string[] typeArgumentNames = null;
            SyntaxNode[] typeArguments = null;
            if (methodInfo.IsGenericMethod)
            {
                typeArgumentNames = methodInfo.GetGenericArguments().Select(type => type.Name).ToArray();
                typeArguments = typeArgumentNames.Select(name => _syntaxGenerator.IdentifierName(name)).ToArray();
            }

            SyntaxNode statement = null;
            if (methodInfo.IsStatic)
            {
                SyntaxNode memberExpression = typeArguments == null ? _syntaxGenerator.IdentifierName(methodInfo.Name) : _syntaxGenerator.GenericName(methodInfo.Name, typeArguments);
                SyntaxNode memberAccessExpression = _syntaxGenerator.MemberAccessExpression(_syntaxGenerator.IdentifierName(methodInfo.DeclaringType.Name), memberExpression);
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
            else
            {
                string declaringTypeName = methodInfo.DeclaringType.Name;
                string fieldName = $"_{char.ToLowerInvariant(declaringTypeName[0])}{declaringTypeName.Substring(1)}";
                SyntaxNode memberExpression = typeArguments == null ? _syntaxGenerator.IdentifierName(methodInfo.Name) : _syntaxGenerator.GenericName(methodInfo.Name, typeArguments);
                SyntaxNode memberAccessExpression = _syntaxGenerator.MemberAccessExpression(_syntaxGenerator.IdentifierName(fieldName), memberExpression);
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

            return _syntaxGenerator.MethodDeclaration(methodInfo.Name, parameters, typeArgumentNames, returnType, Accessibility.Public, statements: new SyntaxNode[] { statement });
        }

        static SyntaxNode CreateEvent(EventInfo eventInfo)
        {
            return _syntaxGenerator.EventDeclaration(eventInfo.Name, CreateTypeSyntax(eventInfo.EventHandlerType), Accessibility.Public);
        }

        static string CreateInterface(string outputNamespace, Type type, HashSet<string> namespaceNames, List<MethodInfo> methodInfos, List<PropertyInfo> propertyInfos, List<EventInfo> eventInfos)
        {
            // Events
            IEnumerable<SyntaxNode> eventDeclarations = eventInfos.Select(CreateEventDeclaration);

            // Properties
            IEnumerable<SyntaxNode> propertyDeclarations = propertyInfos.Select(CreatePropertyDeclaration);

            // Methods
            IEnumerable<SyntaxNode> methodDeclarations = methodInfos.Select(CreateMethodDeclaration);

            // Class
            SyntaxNode interfaceDeclaration = _syntaxGenerator.InterfaceDeclaration($"I{type.Name}Service", accessibility: Accessibility.Public, members: propertyDeclarations.Concat(methodDeclarations).Concat(eventDeclarations));

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

        static SyntaxNode CreateEventDeclaration(EventInfo eventInfo)
        {
            return _syntaxGenerator.EventDeclaration(eventInfo.Name, CreateTypeSyntax(eventInfo.EventHandlerType));
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
                parameters.Add(_syntaxGenerator.ParameterDeclaration(parameterInfo.Name, CreateTypeSyntax(parameterInfo.ParameterType)));
            }

            string[] typeParameters = null;
            if (methodInfo.IsGenericMethod)
            {
                typeParameters = methodInfo.GetGenericArguments().Select(type => type.Name).ToArray();
            }

            return _syntaxGenerator.MethodDeclaration(methodInfo.Name, parameters, typeParameters, returnType);
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

        static List<EventInfo> GetEventInfos(Type type)
        {
            List<EventInfo> result = new List<EventInfo>();

            // Add event infos
            EventInfo[] publicProperties = type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (EventInfo publicEvent in publicProperties)
            {
                // TODO what kinds of events have special names?
                if (publicEvent.IsSpecialName)
                {
                    continue;
                }

                result.Add(publicEvent);
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

        static HashSet<string> GetNamesOfNamespacesUsed(IEnumerable<MethodInfo> methodInfos, IEnumerable<PropertyInfo> propertyInfos, IEnumerable<EventInfo> eventInfos)
        {
            HashSet<string> result = new HashSet<string>();

            foreach (EventInfo eventInfo in eventInfos)
            {
                // Add namespaceNames for event handler type
                AddnamespaceNamesUsed(result, eventInfo.EventHandlerType);
            }

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
