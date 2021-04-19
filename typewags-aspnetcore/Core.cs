using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DarrenDanielDay.Typeawags
{
    public class DependencyTracker<T>
    {
        internal readonly Dictionary<T, int> TrackedIdMapping = new Dictionary<T, int>();
        internal readonly HashSet<T> EntryPoints = new HashSet<T>();
        internal int CurrentId = 0;
        public ITrackStrategy<T> TrackStrategy { get; set; }
        public void AddEntryPoint(T target) => EntryPoints.Add(target);
        public virtual Dictionary<T, int> Collect()
        {
            var queue = new Queue<T>(EntryPoints);
            while (queue.TryDequeue(out var target))
            {
                if (IsTracked(target) || !TrackStrategy.ShouldTrack(target)) { continue; }
                Track(target);
                foreach (var dependency in TrackStrategy.GetDependenciesOf(target))
                {
                    if (IsTracked(dependency)) { continue; }
                    queue.Enqueue(dependency);
                }
            }
            return new Dictionary<T, int>(TrackedIdMapping);
        }

        protected virtual bool IsTracked(T type) => TrackedIdMapping.ContainsKey(type);

        protected virtual void Track(T type) => TrackedIdMapping.Add(type, NextId());

        protected virtual int NextId() => CurrentId++;
    }

    public interface ITrackStrategy<T>
    {
        IEnumerable<T> GetDependenciesOf(T type);
        bool ShouldTrack(T type);
    }
    public interface INamingStrategy<T>
    {
        string GetSimpleName(T target);
        string GetFullName(T target);
        string GetModuleName(T target);
    }
    public class DefaultTypeTrackStrategy : ITrackStrategy<Type>
    {
        public IEnumerable<Assembly> SearchAssemblies { get; set; }
        private HashSet<Type> SearchTypes = new HashSet<Type>();

        public DefaultTypeTrackStrategy Init()
        {
            foreach (var assembly in SearchAssemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    SearchTypes.Add(TSClientUtils.UnwrapTaskType(type));
                }
            }
            return this;
        }
        public IEnumerable<Type> GetDependenciesOf(Type type)
        {
            type = TSClientUtils.UnwrapTaskType(type);
            if (!ShouldTrack(type)) { yield break; }
            if (type.IsEnum) { yield break; }
            PropertyInfo[] directRefTypes;
            if (type.IsGenericType)
            {
                if (TSClientUtils.IsListType(type))
                {
                    yield return TSClientUtils.ListElementOf(type);
                    yield break;
                }
                var genericTypeDefinition = type.GetGenericTypeDefinition();
                yield return genericTypeDefinition;
                foreach (var genericArgument in genericTypeDefinition.GetGenericArguments())
                {
                    yield return genericArgument;
                }
                foreach (var genericArgument in type.GetGenericArguments())
                {
                    yield return genericArgument;
                }
                directRefTypes = type.GetGenericTypeDefinition().GetProperties();
            }
            else
            {
                directRefTypes = type.GetProperties();
            }
            foreach (var property in directRefTypes)
            {
                yield return property.PropertyType;
            }
        }

        public bool ShouldTrack(Type type)
        {
            return TSClientUtils.IsListType(type) || type.IsGenericParameter || type.IsGenericTypeDefinition || type.IsGenericType || SearchTypes.Contains(type);
        }
    }
    public class DefaultCSharpTypeNamingStrategy : INamingStrategy<Type>
    {
        private static string NormalizeTypeName(string name) => name?.Split('`')?.FirstOrDefault();

        public string GetFullName(Type target)
        {
            return NormalizeTypeName(target.FullName) ?? GetSimpleName(target);
        }

        public string GetModuleName(Type target)
        {
            return target.IsGenericParameter ? GetFullName(target.DeclaringType) : target.Namespace;
        }

        public string GetSimpleName(Type target)
        {
            return NormalizeTypeName(target.Name);
        }
    }

    public static class TSClientUtils
    {
        private static readonly List<Type> NumberTypes = new List<Type> { typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal) };
        private static readonly List<Type> StringTypes = new List<Type> { typeof(char), typeof(string) };
        private static readonly List<Type> BooleanTypes = new List<Type> { typeof(bool) };
        private static readonly List<Type> DateTypes = new List<Type> { typeof(DateTime) };
        private static readonly List<Type> DynamicTypes = new List<Type> { typeof(object) };
        private static readonly List<Type> CommonTypes = new List<List<Type>>
        { NumberTypes, StringTypes, BooleanTypes, DateTypes, DynamicTypes }.Aggregate(new List<Type>() as IEnumerable<Type>, (result, current) => current.Aggregate(result, (result, current) => result.Append(current))).ToList();
        public static bool IsCommonType(Type type)
        {
            return CommonTypes.Contains(type);
        }
        public static bool IsListType(Type type)
        {
            return typeof(IEnumerable).IsAssignableFrom(type);
        }
        public static Type ListElementOf(Type type)
        {
            return type.IsArray ? type.GetElementType() : type.GetGenericArguments().FirstOrDefault() ?? typeof(object);
        }
        public static Type UnwrapTaskType(Type type) => typeof(Task).IsAssignableFrom(type) ? UnwrapTaskType(type.GetGenericArguments().FirstOrDefault() ?? typeof(void)) : type;
        public static string GetNumberFormat(Type type)
        {
            if (NumberTypes.Contains(type))
            {
                return type switch
                {
                    var t when t == typeof(long) => "int64",
                    var t when t == typeof(float) => "float32",
                    var t when t == typeof(double) || t == typeof(decimal) => "float64",
                    _ => "int32"
                };
            }
            throw new ArgumentException();
        }
        public static BaseCommonType GetBaseCommonType(Type type)
            => type switch
            {
                var t when NumberTypes.Contains(t) => new NumberType { Format = GetNumberFormat(t) },
                var t when StringTypes.Contains(t) => new StringType { Format = "plain-text" },
                var t when DateTypes.Contains(t) => new StringType { Format = "date" },
                var t when BooleanTypes.Contains(t) => new BooleanType { },
                var t when DynamicTypes.Contains(t) => new DynamicType { },
                _ => throw new ArgumentException()
            };
    }

    public class TypeDefinitionTracker : DependencyTracker<Type>
    {
        public interface IPropertyStrategy
        {
            IEnumerable<SchemaItem> GetSchemaItems(Type type);
        }
        public interface IEnumStrategy
        {
            BaseEnumType GetEnumTypeFor(Type type);
        }
        private class DefaultPropertyStrategy : IPropertyStrategy
        {
            public DefaultPropertyStrategy(TypeDefinitionTracker tracker)
            {
                Tracker = tracker;
            }

            public TypeDefinitionTracker Tracker { get; }

            public IEnumerable<SchemaItem> GetSchemaItems(Type type)
            {
                foreach (var property in type.GetProperties())
                {
                    var propertyType = property.PropertyType;
                    if (Tracker.TrackStrategy.ShouldTrack(propertyType) || TSClientUtils.IsCommonType(propertyType))
                    {
                        yield return new SchemaItem { Key = $@"{Uncapitalize(property.Name)}", Value = Tracker.GetTypeReferenceFor(propertyType) };
                    }
                }
            }
            internal string Uncapitalize(string s) => @$"{s.Substring(0, 1).ToLower()}{s.Substring(1)}";
        }
        private class DefaultEnumStrategy : IEnumStrategy
        {
            public DefaultEnumStrategy(TypeDefinitionTracker tracker)
            {
                Tracker = tracker;
            }
            public TypeDefinitionTracker Tracker { get; }

            public BaseEnumType GetEnumTypeFor(Type type)
            {
                var enums = Enum.GetNames(type).Select(name => new { Value = Convert.ToInt32(Enum.Parse(type, name)), Name = name });
                List<NumberEnumDescriber> mapping = new List<NumberEnumDescriber>();
                foreach (var enumValue in enums)
                {
                    string enumName = enumValue.Name;
                    mapping.Add(new NumberEnumDescriber { Name = enumName, Value = enumValue.Value, Description = type.GetField(enumName).GetCustomAttribute<DescriptionAttribute>()?.Description });
                }
                return type switch
                {
                    var _ when Enum.GetUnderlyingType(type) == typeof(int) => new NumberEnumType { Names = Tracker.GetNameDescriber(type), Mapping = mapping },
                    _ => throw new NotImplementedException(),
                };
            }
        }
        public readonly Dictionary<int, BaseType> IdToBaseType = new Dictionary<int, BaseType>();
        public INamingStrategy<Type> NamingStrategy { get; set; }
        public IPropertyStrategy PropertyStrategy { get; set; }
        public IEnumStrategy EnumStrategy { get; set; }
        public override Dictionary<Type, int> Collect()
        {
            TrackStrategy ??= new DefaultTypeTrackStrategy();
            return base.Collect();
        }
        protected virtual TypeNameDescriber GetNameDescriber(Type type)
        {
            NamingStrategy ??= new DefaultCSharpTypeNamingStrategy();
            return new TypeNameDescriber
            {
                Simple = NamingStrategy.GetSimpleName(type),
                Module = NamingStrategy.GetModuleName(type),
                Full = NamingStrategy.GetFullName(type)
            };
        }
        internal void TrackBaseTypeFor(Type type, int id)
        {
            BaseType definition = GetTypeDefinitionFor(TSClientUtils.UnwrapTaskType(type), id);

            if (definition != null)
                IdToBaseType.Add(id, definition);
        }
        internal BaseType GetTypeDefinitionFor(Type type, int id)
        {
            PropertyStrategy ??= new DefaultPropertyStrategy(this);
            NamingStrategy ??= new DefaultCSharpTypeNamingStrategy();
            EnumStrategy ??= new DefaultEnumStrategy(this);
            return type switch
            {
                var _ when TSClientUtils.IsCommonType(type) => null,
                var _ when TSClientUtils.IsListType(type) => null,
                { IsEnum: true } => GetEnumTypeFor(type, id),
                { IsGenericTypeDefinition: true } => new GenericType
                {
                    Id = id,
                    Names = GetNameDescriber(type),
                    GenericParameters = type.GetGenericArguments().Select(GetTypeReferenceFor).ToList(),
                    Fields = PropertyStrategy.GetSchemaItems(type).ToList()
                },
                { IsGenericType: true } => null,
                { IsGenericParameter: true } => new GenericParameterType
                {
                    Id = id,
                    DefinedIn = TrackedIdMapping[type.DeclaringType],
                    Name = NamingStrategy.GetSimpleName(type),
                    Names = GetNameDescriber(type),
                    Constraints = new GenericParameterConstraint { Extends = type.GetGenericParameterConstraints().Select(constraint => GetTypeReferenceFor(constraint)).ToList() }
                },
                { IsGenericMethodParameter: true } => throw new NotImplementedException(),
                { } => new StructType { Id = id, Names = GetNameDescriber(type), Schema = PropertyStrategy.GetSchemaItems(type).ToList() },
                null => throw new ArgumentNullException(),
            };
        }
        internal BaseEnumType GetEnumTypeFor(Type type, int id)
        {
            BaseEnumType baseEnumType = EnumStrategy.GetEnumTypeFor(type);
            baseEnumType.Id = id;
            return baseEnumType;
        }
        public Dictionary<int, BaseType> GenerateTypeDefinitions()
        {
            foreach (var (type, id) in TrackedIdMapping)
            {
                TrackBaseTypeFor(type, id);
            }
            return new Dictionary<int, BaseType>(IdToBaseType);
        }

        public BaseType GetTypeReferenceFor(Type type) => type switch
        {
            var _ when TSClientUtils.IsCommonType(type) => TSClientUtils.GetBaseCommonType(type),
            var _ when TSClientUtils.IsListType(type) => new ArrayType { Item = GetTypeReferenceFor(TSClientUtils.ListElementOf(type)) },
            { IsEnum: true } => new EnumReference { Id = TrackedIdMapping[type] },
            { IsGenericType: true } => new GenericReference
            {
                GenericParameters = type.GetGenericArguments()
                    .Select(genericParameter => genericParameter.IsGenericParameter
                    ? new GenericTypeParameterReference
                    {
                        Id = TrackedIdMapping[genericParameter]
                    } : GetTypeReferenceFor(genericParameter)).ToList(),
                Id = TrackedIdMapping[type.GetGenericTypeDefinition()]
            },
            { IsGenericParameter: true } => new GenericTypeParameterReference { Id = TrackedIdMapping[type] },
            { IsGenericMethodParameter: true } => throw new NotImplementedException(),
            { IsGenericTypeDefinition: true } => throw new NotImplementedException(),
            { } => new StructReference { Id = TrackedIdMapping[type] },
            null => throw new ArgumentNullException(),
        };
    }

    public class AspNetCoreWebAPIInspector
    {
        public AspNetCoreWebAPIInspector(Assembly programAssembly)
        {
            ProgramAssembly = programAssembly;
        }
        public Assembly ProgramAssembly { get; }
        internal TypeDefinitionTracker Tracker;
        internal HashSet<Type> Controllers;
        internal HashSet<MethodInfo> ExcludeGetterAndSetterMethods = new HashSet<MethodInfo>();
        internal void TrackDependencyTypes()
        {
            Tracker = new TypeDefinitionTracker { TrackStrategy = new DefaultTypeTrackStrategy { SearchAssemblies = new List<Assembly> { ProgramAssembly } }.Init() };
            var controllers = Controllers = GetControllers();
            foreach (var controller in controllers)
            {
                foreach (var property in controller.GetProperties())
                {
                    var getMethod = property.GetGetMethod();
                    if (getMethod != null)
                        ExcludeGetterAndSetterMethods.Add(getMethod);
                    var setMethod = property.GetSetMethod();
                    if (setMethod != null)
                        ExcludeGetterAndSetterMethods.Add(setMethod);
                }
            }
            foreach (var controller in controllers)
            {
                var apiMethods = controller.GetMethods().Where(ControllerMethodFilter);
                foreach (var method in apiMethods)
                {
                    if (method.GetCustomAttribute<NonActionAttribute>() != null) continue;
                    foreach (var parameter in method.GetParameters())
                    {
                        Tracker.AddEntryPoint(parameter.ParameterType);
                    }
                    Tracker.AddEntryPoint(TSClientUtils.UnwrapTaskType(method.ReturnType));
                }
            }
            Tracker.Collect();
            Tracker.GenerateTypeDefinitions();
        }

        internal HashSet<Type> GetControllers()
        {
            return new HashSet<Type>(ProgramAssembly.GetTypes().Where(type => typeof(ControllerBase).IsAssignableFrom(type)));
        }

        public IEnumerable<BaseType> GetDefinitions()
        {
            return new HashSet<BaseType>(Tracker.IdToBaseType.Values);
        }

        public IEnumerable<WebAPI> Inspect()
        {
            TrackDependencyTypes();
            var definitions = Tracker.IdToBaseType;
            foreach (var controller in GetControllers())
            {
                var apiMethods = controller.GetMethods().Where(ControllerMethodFilter);
                foreach (var method in apiMethods)
                {
                    var responseType = TSClientUtils.UnwrapTaskType(method.ReturnType);
                    var controllerRouteAttribute = controller.GetCustomAttribute<RouteAttribute>();
                    var methodRouteAttribute = method.GetCustomAttribute<RouteAttribute>();
                    var methodVerbAttribute = method.GetCustomAttribute<HttpMethodAttribute>(true);
                    var requestMethod = WebAPI.GetMethodEnumOf(method);
                    BaseType tsClientResponseType;
                    try
                    {
                        tsClientResponseType = Tracker.GetTypeReferenceFor(responseType);
                    }
                    catch
                    {
                        tsClientResponseType = new DynamicType { };
                    }

                    yield return new WebAPI
                    {
                        Route = $@"/{string.Join('/', PathSplitterRegex.Split(controllerRouteAttribute?.Template?.Replace("[controller]", controller.Name.Replace("Controller", "")) ?? "/").Concat(PathSplitterRegex.Split(methodRouteAttribute?.Template ?? methodVerbAttribute?.Template ?? method.Name)))}",
                        Parameters = method.GetParameters().Select(parameter => new RequestParameter
                        {
                            Name = parameter.Name,
                            ParameterType = Tracker.GetTypeReferenceFor(parameter.ParameterType),
                            ParameterPositionType = RequestParameter.GetParameterPosition(controllerRouteAttribute, parameter, requestMethod),
                        }).ToList(),
                        Name = method.Name,
                        ResponseType = tsClientResponseType,
                        RequestMethodType = requestMethod,
                    };
                }
            }
        }
        internal bool ControllerMethodFilter(MethodInfo method) => method switch
        {
            { IsAbstract: false, IsStatic: false, IsConstructor: false, IsPublic: true, } when Controllers.Contains(method.DeclaringType) && !ExcludeGetterAndSetterMethods.Contains(method) => true,
            _ => false
        };

        internal Regex PathSplitterRegex = new Regex(@"\\|/");

        public static InspectResult AllInOne(Assembly programAssembly)
        {
            var inspector = new AspNetCoreWebAPIInspector(programAssembly);
            var apis = inspector.Inspect().ToList();
            var definitions = inspector.GetDefinitions();
            return new InspectResult { Apis = apis, Definitions = definitions.ToList() };
        }
    }

    #region Type System
    public class DynamicType : BaseCommonType
    {
        public DynamicType()
        {
            Type = "any";
        }
    }

    public class BaseType
    {
        public string Type { get; set; }
        public string Kind { get; set; }
    }

    public class BaseCommonType : BaseType
    {
        public BaseCommonType()
        {
            Kind = "common";
        }
    }

    public class StringType : BaseCommonType
    {
        public string Format { get; set; }
        public StringType()
        {
            Type = "string";
        }
    }

    public class NumberType : BaseCommonType
    {
        public string Format { get; set; }
        public NumberType()
        {
            Type = "number";
        }
    }

    public class BooleanType : BaseCommonType
    {
        public BooleanType()
        {
            Type = "boolean";
        }
    }

    public class ArrayType : BaseCommonType
    {
        public BaseType Item { get; set; }
        public ArrayType()
        {
            Type = "array";
        }
    }


    public class BaseCustomType : BaseType
    {
        public int Id { get; set; }
        public TypeNameDescriber Names { get; set; }
        public BaseCustomType()
        {
            Kind = "custom";
        }
    }

    public class TypeNameDescriber
    {
        public string Simple { get; set; }
        public string Full { get; set; }
        public string Module { get; set; }
    }

    public class BaseEnumType : BaseCustomType
    {
        public string DataType { get; set; }
        public BaseEnumType()
        {
            Type = "enum";
        }
    }

    public class StringEnumType : BaseEnumType
    {
        public List<string> Enums { get; set; }
        public StringEnumType()
        {
            DataType = "string";
        }
    }

    public class NumberEnumType : BaseEnumType
    {
        public List<NumberEnumDescriber> Mapping { get; set; }
        public NumberEnumType()
        {
            DataType = "number";
        }
    }

    public class NumberEnumDescriber
    {
        public string Name { get; set; }
        public int Value { get; set; }
        public string Description { get; set; }
    }

    public class ReferenceType : BaseType
    {
        public int Id { get; set; }
        public ReferenceType()
        {
            Type = "reference";
        }
    }

    public class EnumReference : ReferenceType
    {
        public EnumReference()
        {
            Kind = "enum";
        }
    }

    public class StructType : BaseCustomType
    {
        public List<SchemaItem> Schema { get; set; }
        public StructType()
        {
            Type = "struct";
        }
    }

    public class SchemaItem
    {
        public string Key { get; set; }
        public BaseType Value { get; set; }
    }

    public class StructReference : ReferenceType
    {
        public StructReference()
        {
            Kind = "struct-reference";
        }
    }

    public class GenericType : BaseCustomType
    {
        public List<BaseType> GenericParameters { get; set; }
        public List<SchemaItem> Fields { get; set; }
        public GenericType()
        {
            Type = "generic";
        }
    }

    public class GenericParameterType : BaseCustomType
    {
        public int DefinedIn { get; set; }
        public string Name { get; set; }
        public GenericParameterConstraint Constraints { get; set; }
        public GenericParameterType()
        {
            Type = "generic-parameter";
        }
    }

    public class GenericParameterConstraint
    {
        public List<BaseType> Extends { get; set; }
        public List<BaseType> Super { get; set; }
    }

    public class GenericTypeParameterReference : ReferenceType
    {
        public GenericTypeParameterReference()
        {
            Kind = "generic-parameter";
        }
    }

    public class GenericReference : ReferenceType
    {
        public List<BaseType> GenericParameters { get; set; }
        public GenericReference()
        {
            Kind = "generic-reference";
        }
    }
    #endregion

    #region API Definition
    public class RequestParameter
    {
        public enum ParameterPositionEnum
        {
            Query,
            Route,
            Body,
            Form,
            Header,
        }
        internal static ParameterPositionEnum GetParameterPosition(RouteAttribute routeAttribute, ParameterInfo parameter, WebAPI.RequestMethodEnum requestMethod)
        {
            var name = parameter.Name;
            var paramAttrs = parameter.GetCustomAttributes().ToList();
            if (paramAttrs.Count != 0)
            {
                return paramAttrs.FirstOrDefault() switch
                {
                    FromBodyAttribute _ => ParameterPositionEnum.Body,
                    FromFormAttribute _ => ParameterPositionEnum.Form,
                    FromHeaderAttribute _ => ParameterPositionEnum.Header,
                    FromQueryAttribute _ => ParameterPositionEnum.Query,
                    FromRouteAttribute _ => ParameterPositionEnum.Route,
                    _ => throw new ArgumentException(),
                };
            }
            if (routeAttribute != null)
            {
                if (routeAttribute.Template.ToLower().Contains($@"{{{name.ToLower()}}}"))
                {
                    return ParameterPositionEnum.Route;
                }
            }
            return requestMethod switch
            {
                WebAPI.RequestMethodEnum.GET => ParameterPositionEnum.Query,
                WebAPI.RequestMethodEnum.POST => ParameterPositionEnum.Body,
                WebAPI.RequestMethodEnum.PUT => ParameterPositionEnum.Body,
                WebAPI.RequestMethodEnum.PATCH => ParameterPositionEnum.Body,
                WebAPI.RequestMethodEnum.DELETE => ParameterPositionEnum.Body,
                WebAPI.RequestMethodEnum.HEAD => throw new NotImplementedException(),
                WebAPI.RequestMethodEnum.OPTIONS => throw new NotImplementedException(),
                _ => throw new ArgumentException(),
            };
        }
        public string Name { get; set; }
        public BaseType ParameterType { get; set; }
        internal ParameterPositionEnum ParameterPositionType { get; set; }
        public string ParameterPosition { get { return Enum.GetName(typeof(ParameterPositionEnum), ParameterPositionType).ToLower(); } }
    }
    public class WebAPI
    {
        public enum RequestMethodEnum
        {
            GET,
            POST,
            PUT,
            PATCH,
            DELETE,
            HEAD,
            OPTIONS,
        }
        internal static RequestMethodEnum GetMethodEnumOf(MethodInfo method)
        {
            var attributes = method.GetCustomAttributes().Where(attribute => typeof(HttpMethodAttribute).IsAssignableFrom(attribute.GetType())).ToList();
            return attributes switch
            {
                { Count: 0 } => RequestMethodEnum.GET,
                _ => attributes.First() switch
                {
                    HttpGetAttribute _ => RequestMethodEnum.GET,
                    HttpPostAttribute _ => RequestMethodEnum.POST,
                    HttpPutAttribute _ => RequestMethodEnum.PUT,
                    HttpPatchAttribute _ => RequestMethodEnum.PATCH,
                    HttpDeleteAttribute _ => RequestMethodEnum.DELETE,
                    HttpHeadAttribute _ => RequestMethodEnum.HEAD,
                    HttpOptionsAttribute _ => RequestMethodEnum.OPTIONS,
                    _ => throw new ArgumentException(),
                },

            };
        }
        public string Route { get; set; }
        public string Name { get; set; }
        internal RequestMethodEnum RequestMethodType { get; set; }
        public string RequestMethod { get { return Enum.GetName(typeof(RequestMethodEnum), RequestMethodType).ToLower(); } }
        public List<RequestParameter> Parameters { get; set; }
        public BaseType ResponseType { get; set; }
    }

    public class InspectResult
    {
        public List<BaseType> Definitions { get; set; }
        public List<WebAPI> Apis { get; set; }
    }
    #endregion
}
