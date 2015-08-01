#region Copyright (c) 2010 Atif Aziz. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//    http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
#endregion

#region Assembly Information

using System.Reflection;
using System.Runtime.InteropServices;
using System.Web;
using Elmah.Bootstrapper;

[assembly: AssemblyTitle("Elmah.Bootstrapper")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("ELMAH")]
[assembly: AssemblyCopyright("Copyright \u00a9 2010 Atif Aziz. All rights reserved.")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: AssemblyVersion("1.0.18628.0")]
[assembly: AssemblyFileVersion("1.0.18628.620")]

#if DEBUG
[assembly: AssemblyConfiguration("DEBUG")]
#else
[assembly: System.Reflection.AssemblyConfiguration("RELEASE")]
#endif

#endregion

[assembly: PreApplicationStartMethod(typeof(Ignition), "Start")]

namespace Elmah.Bootstrapper
{
    #region Imports

    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.ComponentModel.Design;
    using System.Configuration;
    using System.IO;
    using System.Linq;
    using System.Net.Mail;
    using System.Runtime.CompilerServices;
    using System.Text.RegularExpressions;
    using System.Web;
    using System.Web.Caching;
    using System.Web.Hosting;
    using System.Xml;
    using Assertions;

    #endregion

    sealed class ErrorLogHandlerMappingModule : HttpModuleBase
    {
        ErrorLogPageFactory _errorLogPageFactory;
        ErrorLogPageFactory HandlerFactory => _errorLogPageFactory ?? (_errorLogPageFactory = new ErrorLogPageFactory());

        protected override void OnInit(HttpApplication application)
        {
            application.Subscribe(h => application.PostMapRequestHandler += h, OnPostMapRequestHandler);
            application.Subscribe(h => application.EndRequest += h, OnEndRequest);
        }

        void OnPostMapRequestHandler(HttpContextBase context)
        {
            var request = context.Request;

            var url = request.FilePath;
            var match = Regex.Match(url, @"(/(?:.*\b)?(?:elmah|errors|errorlog))(/.+)?$",
                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                             .BindNum((fst, snd) => new { fst.Success, Url = fst.Value, PathInfo = snd.Value });

            if (!match.Success)
                return;

            url = match.Url;
            // ReSharper disable once PossibleNullReferenceException
            var queryString = request.Url.Query;

            context.RewritePath(url, match.PathInfo,
                                queryString.Length > 0 && queryString[0] == '?'
                                ? queryString.Substring(1)
                                : queryString);

            var pathTranslated = request.PhysicalApplicationPath;
            var factory = HandlerFactory;
            var handler = factory.GetHandler(context, request.HttpMethod, url, pathTranslated);
            if (handler == null)
                return;

            context.Items[this] = new ContextState
            {
                Handler = handler,
                HandlerFactory = factory,
            };

            context.Handler = handler;
        }

        void OnEndRequest(HttpContextBase context)
        {
            var state = context.Items[this] as ContextState;
            state?.HandlerFactory.ReleaseHandler(state.Handler);
        }

        sealed class ContextState
        {
            public IHttpHandler Handler;
            public IHttpHandlerFactory HandlerFactory;
        }
    }

    public static class Ignition
    {
        static readonly object Lock = new object();

        static bool _registered;

        public static void Start()
        {
            lock (Lock)
            {
                if (_registered)
                    return;
                StartImpl();
                _registered = true;
            }
        }

        static void StartImpl()
        {
            // TODO Consider what happens if registration fails halfway

            ServiceCenter.Current = GetServiceProvider;

            foreach (var type in DefaultModuleTypeSet)
                RegisterModule(type);
        }

        static void RegisterModule(Type moduleType)
        {
#if NET40
            Microsoft.Web.Infrastructure.DynamicModuleHelper.DynamicModuleUtility.RegisterModule(moduleType);
#else
            HttpApplication.RegisterModule(moduleType);
#endif
        }

        static IEnumerable<Type> DefaultModuleTypeSet
        {
            get
            {
                yield return typeof(ErrorLogSecurityModule);
                yield return typeof(ErrorLogModule);
                yield return typeof(ErrorMailModule);
                yield return typeof(ErrorFilterModule);
                yield return typeof(ErrorTweetModule);
                yield return typeof(ErrorLogHandlerMappingModule);
                yield return typeof(InitializationModule);
            }
        }

        public static IServiceProvider GetServiceProvider(object context)
        {
            return GetServiceProvider(AsHttpContextBase(context));
        }

        static HttpContextBase AsHttpContextBase(object context)
        {
            if (context == null)
                return null;
            var httpContextBase = context as HttpContextBase;
            if (httpContextBase != null)
                return httpContextBase;
            var httpContext = context as HttpContext;
            return httpContext == null
                 ? null
                 : new HttpContextWrapper(httpContext);
        }

        static readonly object ContextKey = new object();

        static IServiceProvider GetServiceProvider(HttpContextBase context)
        {
            var sp = context?.Items[ContextKey] as IServiceProvider;
            if (sp != null)
                return sp;

            var container = new ServiceContainer(ServiceCenter.Default(context));

            if (context != null)
            {
                var cachedErrorLog = new ErrorLog[1];
                container.AddService(typeof (ErrorLog), delegate
                {
                    return cachedErrorLog[0] ?? (cachedErrorLog[0] =  ErrorLogFactory());
                });

                context.Items[ContextKey] = container;
            }

            return container;
        }

        static Func<ErrorLog> _errorLogFactory;
        static Func<ErrorLog> ErrorLogFactory => _errorLogFactory ?? (_errorLogFactory = CreateErrorLogFactory());

        static Func<ErrorLog> CreateErrorLogFactory()
        {
            string xmlLogPath;
            return ShouldUseErrorLog(config => new SqlErrorLog(config))
                ?? ShouldUseErrorLog(config => new SQLiteErrorLog(config))
                ?? ShouldUseErrorLog(config => new SqlServerCompactErrorLog(config))
                ?? ShouldUseErrorLog(config => new OracleErrorLog(config))
                ?? ShouldUseErrorLog(config => new MySqlErrorLog(config))
                ?? ShouldUseErrorLog(config => new PgsqlErrorLog(config))
                // ReSharper disable once AssignNullToNotNullAttribute
                ?? (Directory.Exists(xmlLogPath = HostingEnvironment.MapPath("~/App_Data/errors/xmlstore"))
                 ? (() => new XmlFileErrorLog(xmlLogPath))
                 : new Func<ErrorLog>(() => new MemoryErrorLog()));
        }

        static Func<ErrorLog> ShouldUseErrorLog<T>(Func<IDictionary, T> factory) where T : ErrorLog
        {
            var logTypeName = typeof(T).Name;

            const string errorlogSuffix = "ErrorLog";
            if (logTypeName.EndsWith(errorlogSuffix, StringComparison.OrdinalIgnoreCase))
                logTypeName = logTypeName.Substring(0, logTypeName.Length - errorlogSuffix.Length);

            var csName = "elmah:" + logTypeName;
            var css = ConfigurationManager.ConnectionStrings[csName];
            if (string.IsNullOrEmpty(css?.ConnectionString))
                return null;

            var config = new Hashtable
            {
                { "connectionString", css.ConnectionString}
            };

            foreach (var e in Configuration.Default.GetSettings(logTypeName))
                config[e.Key] = e.Value;

            return () =>
            {
                ErrorLog log = factory(/* copy */ new Hashtable(config));
                if (string.IsNullOrEmpty(log.ApplicationName))
                    log.ApplicationName = ApplicationName;
                return log;
            };
        }

        static string _applicationName;
        static string ApplicationName => _applicationName ?? (_applicationName = Configuration.Default.GetSetting("applicationName"));
    }

    sealed class Configuration
    {
        readonly NameValueCollection _settings;

        public static readonly Configuration Default = new Configuration(ConfigurationManager.AppSettings);

        Configuration(NameValueCollection settings) { _settings = settings; }

        public string GetSetting(string key) { return _settings["elmah:" + key]; }

        public IEnumerable<KeyValuePair<string, string>> GetSettings(string scope)
        {
            var prefix = "elmah:" + scope + ":";
            return
                from key in _settings.AllKeys
                where key.Length > prefix.Length
                   && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                select new KeyValuePair<string, string>(key.Substring(prefix.Length), _settings[key]);
        }
    }

    public class HttpModuleInitializingEventArgs : EventArgs
    {
        List<IDisposable> _disposables;

        public IHttpModule Module { get; }
        public HttpApplication Application { get; }
        public bool HasDisposables => _disposables?.Count > 0;
        public IEnumerable<IDisposable> Disposables => HasDisposables
                                                     ? _disposables.AsEnumerable()
                                                     : Enumerable.Empty<IDisposable>();

        public HttpModuleInitializingEventArgs(IHttpModule module, HttpApplication application)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            if (application == null) throw new ArgumentNullException(nameof(application));
            Module = module;
            Application = application;
        }

        public void OnDispose(IDisposable disposable)
        {
            if (disposable == null) throw new ArgumentNullException(nameof(disposable));
            (_disposables ?? (_disposables = new List<IDisposable>())).Add(disposable);
        }

        public void OnDispose(Action action) => OnDispose(new DelegatingDisposable(action));
    }

    sealed class DelegatingDisposable : IDisposable
    {
        Action OnDispose { get; }
        public DelegatingDisposable(Action onDispose) { OnDispose = onDispose; }
        public void Dispose() => OnDispose?.Invoke();
    }

    public static class HttpModuleInitialization
    {
        public static event EventHandler<HttpModuleInitializingEventArgs> Initializing;
        internal static void OnInitializing(HttpModuleInitializingEventArgs args) { Initializing?.Invoke(args.Module, args); }
    }

    static class App
    {
        public static void OnModuleEvent<T, THandler, TEventArgs>(
            Action<T, THandler> subscriber,
            Action<T, THandler> unsubcriber,
            Func<Action<object, TEventArgs>, THandler> converter,
            Action<T, TEventArgs> handler)
        {
            if (subscriber == null) throw new ArgumentNullException(nameof(subscriber));
            if (unsubcriber == null) throw new ArgumentNullException(nameof(unsubcriber));
            if (converter == null) throw new ArgumentNullException(nameof(converter));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            HttpModuleInitialization.Initializing += (_, args) =>
            {
                var mhs = 
                    from module in args.Application.Modules.AsEnumerable().Select(e => e.Value).OfType<T>()
                    select new
                    {
                        Module  = module,
                        Handler = converter((sender, a) => handler((T) sender, a)),
                    };

                foreach (var mh in mhs)
                {
                    subscriber(mh.Module, mh.Handler);
                    args.OnDispose(() => unsubcriber(mh.Module, mh.Handler));
                }
            };
        }

        public static void OnError(Action<Exception> handler) => OnErrorImpl(handler);
        public static void OnError(Action<HttpContextBase> handler) => OnErrorImpl(handler2: handler);
        public static void OnError(Action<Exception, HttpContextBase> handler) => OnErrorImpl(handler3: handler);

        static void OnErrorImpl(Action<Exception> handler1 = null,
                                Action<HttpContextBase> handler2 = null,
                                Action<Exception, HttpContextBase> handler3 = null)
        {
            HttpModuleInitialization.Initializing += (_, args) =>
            {
                args.Application.Error += (sender, __) =>
                {
                    var app = (HttpApplication)sender;
                    handler1?.Invoke(app.Server.GetLastError());
                    handler2?.Invoke(new HttpContextWrapper(app.Context));
                    handler3?.Invoke(app.Server.GetLastError(), new HttpContextWrapper(app.Context));
                };
            };
        }
    }

    /* e.g.

    static class WebApp
    {
        static void Init()
        {
            App.OnModuleEvent(
                (m, h) => m.Filtering += h,
                (m, h) => m.Filtering -= h,
                h => new ExceptionFilterEventHandler((sender, args) => h(sender, args)),
                (IExceptionFiltering sender, ExceptionFilterEventArgs args) =>
                {                    
                    // TODO event handling code
                });

            App.OnModuleEvent(
                (m, h) => m.Mailing += h,
                (m, h) => m.Mailing -= h,
                h => new ErrorMailEventHandler((sender, args) => h(sender, args)), 
                (Elmah.ErrorMailModule sender, ErrorMailEventArgs args) =>
                {
                    // TODO event handling code
                });

            App.OnModuleEvent(
                (m, h) => m.Mailing += h,
                (m, h) => m.Mailing -= h,
                h => new ErrorMailEventHandler((sender, args) => h(sender, args)),
                (Elmah.ErrorMailModule sender, ErrorMailEventArgs args) =>
                {
                    // TODO event handling code
                });

            App.OnModuleEvent(
                (m, h) => m.DisposingMail += h,
                (m, h) => m.DisposingMail -= h,
                h => new ErrorMailEventHandler((sender, args) => h(sender, args)),
                (Elmah.ErrorMailModule sender, ErrorMailEventArgs args) =>
                {
                    // TODO event handling code
                });

            App.OnModuleEvent(
                (m, h) => m.Logged += h,
                (m, h) => m.Logged -= h,
                h => new ErrorLoggedEventHandler((sender, args) => h(sender, args)),
                (Elmah.ErrorLogModule sender, ErrorLoggedEventArgs args) =>
                {
                    // TODO event handling code
                });
        }
    }

    */

    sealed class InitializationModule : IHttpModule
    {
        IDisposable[] _disposables;

        public void Init(HttpApplication context)
        {
            var args = new HttpModuleInitializingEventArgs(this, context);
            HttpModuleInitialization.OnInitializing(args);
            if (args.HasDisposables)
                _disposables = args.Disposables.ToArray();
        }

        public void Dispose()
        {
            if ((_disposables?.Length ?? 0) == 0)
                return;
            // ReSharper disable once PossibleNullReferenceException
            var disposables = new IDisposable[_disposables.Length];
            _disposables?.CopyTo(disposables, 0);
            _disposables = null;
            foreach (var disposable in disposables)
                try { disposable?.Dispose(); } catch { /* ignored */ }
        }
    }

    public static class LoggedException
    {
        static readonly ConditionalWeakTable<Exception, ErrorLogEntry> Table = new ConditionalWeakTable<Exception, ErrorLogEntry>();

        internal static void Add(ErrorLogEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (entry.Error.Exception == null) throw new ArgumentException(null, nameof(entry));
            Table.Add(entry.Error.Exception, entry);
        }

        public static ErrorLogEntry RecallErrorLogEntry(Exception exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            ErrorLogEntry entry;
            return Table.TryGetValue(exception, out entry) ? entry : null;
        }
    }

    sealed class ErrorLogModule : Elmah.ErrorLogModule
    {
        protected override void OnLogged(ErrorLoggedEventArgs args)
        {
            LoggedException.Add(args.Entry);
            base.OnLogged(args);
        }
    }

    public static class ErrorTextFormatterFactory
    {
        public static Func<ErrorTextFormatter> Default => () => new ErrorMailHtmlFormatter();

        static Func<ErrorTextFormatter> _current;

        public static Func<ErrorTextFormatter> Current
        {
            get { return _current ?? Default; }
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                _current = value;
            }
        }
    }

    sealed class ErrorMailModule : Elmah.ErrorMailModule
    {
        protected override ErrorTextFormatter CreateErrorFormatter()
        {
            return ErrorTextFormatterFactory.Current() ?? base.CreateErrorFormatter();
        }

        protected override object GetConfig()
        {
            var config = new Hashtable();
            foreach (var e in Configuration.Default.GetSettings("errorMail"))
                config[e.Key] = e.Value;
            if (config.Count == 0)
                return null;
            if (Recipients.To.Count > 0)
                config["to"] = Recipients.To.ToString();
            return config;
        }

        protected override void OnMailing(ErrorMailEventArgs args)
        {
            var mail = args.Mail;
            var recipients = Recipients;
            if (recipients != null)
            {
                mail.To.AddRange(recipients.To);
                mail.CC.AddRange(recipients.Cc);
                mail.Bcc.AddRange(recipients.Bcc);
            }
            base.OnMailing(args);
        }

        static readonly string CacheKey = typeof(ErrorMailModule).FullName + ":mail";
        static RecipientsCollection _recipients;

        static RecipientsCollection Recipients =>
            _recipients ?? (_recipients = Load(() => _recipients = null));

        static RecipientsCollection Load(Action onInvalidation)
        {
            const string configPath = "~/Elmah.ErrorMail.config";

            var vpp = HostingEnvironment.VirtualPathProvider;

            var recipients = new RecipientsCollection();

            if (vpp.FileExists(configPath))
            {
                var entries =
                    from line in vpp.GetFile(configPath).ReadLines()
                    select line.Trim() into line
                    where line.Length > 0 && line[0] != '#'
                    select Regex.Match(line, @"^(?:(to|cc|bcc):)?(.+)") into m
                    where m.Success
                    select m.Groups into gs
                    let id = gs[1].Success ? gs[1].Value[0] : 't'
                    select new
                    {
                        Collection = id == 't' ? recipients.To
                                   : id == 'c' ? recipients.Cc
                                   : recipients.Bcc,
                        Addresses  = gs[2].Value,
                    };

                foreach (var e in entries)
                    e.Collection.Add(e.Addresses);
            }

            HttpRuntime.Cache.Insert(CacheKey, CacheKey,
                                     vpp.GetCacheDependency(configPath, new[] { configPath }, DateTime.Now),
                                     delegate { onInvalidation(); });

            return recipients;
        }

        sealed class RecipientsCollection
        {
            public readonly MailAddressCollection To  = new MailAddressCollection();
            public readonly MailAddressCollection Cc  = new MailAddressCollection();
            public readonly MailAddressCollection Bcc = new MailAddressCollection();
        }
    }

    sealed class ErrorLogSecurityModule : HttpModuleBase, IRequestAuthorizationHandler
    {
        static readonly string CacheKey = typeof(ErrorLogSecurityModule).FullName + ":predicate";

        static Predicate<HttpContextBase> _authority;

        public bool Authorize(HttpContext context)
        {
            return Authority(new HttpContextWrapper(context));
        }

        static Predicate<HttpContextBase> Authority =>
            _authority ?? (_authority = Load(() => _authority = null));

        static Predicate<HttpContextBase> Load(Action onInvalidation)
        {
            const string configPath = "~/Elmah.Athz.config";

            var vpp = HostingEnvironment.VirtualPathProvider;

            var entries =
                from path in new[] { configPath }
                where vpp.FileExists(path)
                from line in vpp.GetFile(configPath).ReadLines()
                select line.Trim()
                into line
                where line.Length > 0 && line[0] != '#'
                select Regex.Match(line, @"
                            ^
                            (?<not>!?)                               # not
                            (   (?<role> \^) (?<name>\w[\w\p{P}\d]*) # role (^) + name
                            |   (?<name> [*?] | \w[\w\p{P}\d]*     ) # authenticated (*) | anonymous (?) | username
                            |   (?<name> @local)                     # special
                            )
                            (?:\s*\#.*)?                             # comment
                            $", RegexOptions.CultureInvariant
                              | RegexOptions.IgnorePatternWhitespace)
                into m
                where m.Success
                select m.Groups into gs
                select new
                {
                    Denial = gs["not" ].Length > 0,
                    IsRole = gs["role"].Success,
                    Name   = gs["name"].Value,
                };

            var acl = entries.ToLookup(e => e.Denial,
                                       e => e.IsRole
                                          ? Predicates.IsInRole(e.Name)
                                          : "*" == e.Name
                                          ? Predicates.IsAuthenticated
                                          : "?" == e.Name
                                          ? Predicates.IsAnonymous
                                          : "@local".Equals(e.Name, StringComparison.OrdinalIgnoreCase)
                                          ? Predicates.IsLocalRequest
                                          : Predicates.IsId(e.Name));

            var denials = acl[true ].ToArray();
            var grants  = acl[false].ToArray();

            Predicate<HttpContextBase> predicate = principal => !denials.Any(p => p(principal)) && grants.Any(p => p(principal));

            HttpRuntime.Cache.Insert(CacheKey, CacheKey,
                                     vpp.GetCacheDependency(configPath, new[] { configPath }, DateTime.Now),
                                     delegate { onInvalidation(); });

            return predicate;
        }

        static class Predicates
        {
            public readonly static Predicate<HttpContextBase> IsAuthenticated = ctx => ctx.User.Identity.IsAuthenticated;
            public readonly static Predicate<HttpContextBase> IsAnonymous     = ctx => !IsAuthenticated(ctx);
            public readonly static Predicate<HttpContextBase> IsLocalRequest  = ctx => ctx.Request.IsLocal;

            public static Predicate<HttpContextBase> IsId(string name)        => ctx => name.Equals(ctx.User.Identity.Name, StringComparison.OrdinalIgnoreCase);
            public static Predicate<HttpContextBase> IsInRole(string name)    => ctx => ctx.User.IsInRole(name);
        }
    }

    sealed class ErrorFilterModule : Elmah.ErrorFilterModule
    {
        static readonly string CacheKey = typeof(ErrorFilterModule).FullName + ":assertion";

        static IAssertion _assertion;

        public override void Init(HttpApplication application)
        {
            var modules =
                from ms in new[]
                {
                    from m in application.Modules.AsEnumerable()
                    select m.Value
                }
                from m in ms.OfType<IExceptionFiltering>()
                select m;

            foreach (var filtering in modules)
                filtering.Filtering += OnErrorModuleFiltering;
        }

        public override IAssertion Assertion =>
            _assertion ?? (_assertion = LoadAssertion(() => _assertion = null) ?? base.Assertion);

        static readonly DelegatingAssertion FalseAssertion = new DelegatingAssertion(_ => false);

        static IAssertion LoadAssertion(Action onInvalidation)
        {
            var configPath = (ConfigurationManager.AppSettings["elmah:errorFilter:assertion"] ?? string.Empty).Trim();
            if (configPath.Length == 0)
                configPath = "~/Elmah.ErrorFilter.config";

            var vpp = HostingEnvironment.VirtualPathProvider;
            var assertion = TryLoadAssertion(() => vpp.TryOpen(configPath)) ?? FalseAssertion;

            HttpRuntime.Cache.Insert(CacheKey, assertion,
                                     vpp.GetCacheDependency(configPath, new[] { configPath }, DateTime.Now),
                                     delegate { onInvalidation(); });

            return assertion;
        }

        static IAssertion TryLoadAssertion(Func<Stream> opener)
        {
            using (var stream = opener() ?? Stream.Null)
            using (var reader = new StreamReader(stream))
            {
                var content = reader.ReadToEnd();
                var trimmed = content.Trim();
                if (trimmed.Length == 0)
                    return null;

                XmlElement element;

                if (trimmed[0] == '<')  // Assume XML
                {
                    var config = new XmlDocument();
                    config.LoadXml(content);
                    element = (XmlElement) config.SelectSingleNode("errorFilter/test/*")
                              ?? (XmlElement) config.SelectSingleNode("test/*")
                              ?? config.DocumentElement;
                }
                else                    // Assume JScript expression
                {
                    var config = new XmlDocument();
                    var expression = config.CreateElement("expression");
                    expression.AppendChild(config.CreateTextNode(content));
                    var jscript = config.CreateElement("jscript");
                    jscript.AppendChild(expression);
                    config.AppendChild(jscript);
                    element = config.DocumentElement;
                }

                return AssertionFactory.Create(element);
            }
        }

        sealed class DelegatingAssertion : IAssertion
        {
            readonly Predicate<object> _predicate;
            public DelegatingAssertion(Predicate<object> predicate) { _predicate = predicate; }
            public bool Test(object context) { return _predicate(context); }
        }
    }

    static class KeyValuePair
    {
        public static KeyValuePair<TKey, TValue> Create<TKey, TValue>(TKey key, TValue value) => new KeyValuePair<TKey,TValue>(key, value);
    }

    static class WebExtensions
    {
        /// <summary>
        /// Helps with subscribing to <see cref="HttpApplication"/> events
        /// but where the handler
        /// </summary>

        public static void Subscribe(this HttpApplication application,
            Action<EventHandler> subscriber,
            Action<HttpContextBase> handler)
        {
            if (application == null) throw new ArgumentNullException(nameof(application));
            if (subscriber == null) throw new ArgumentNullException(nameof(subscriber));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            subscriber((sender, _) => handler(new HttpContextWrapper(((HttpApplication)sender).Context)));
        }

        /// <summary>
        /// Same as <see cref="IHttpHandlerFactory.GetHandler"/> except the
        /// HTTP context is typed as <see cref="HttpContextBase"/> instead
        /// of <see cref="HttpContext"/>.
        /// </summary>

        public static IHttpHandler GetHandler(this IHttpHandlerFactory factory,
            HttpContextBase context, string requestType,
            string url, string pathTranslated)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            return factory.GetHandler(context.ApplicationInstance.Context, requestType, url, pathTranslated);
        }
    }

    static class HttpModuleCollectionExtensions
    {
        public static IEnumerable<KeyValuePair<string, IHttpModule>> AsEnumerable(this HttpModuleCollection modules)
        {
            if (modules == null) throw new ArgumentNullException(nameof(modules));
            return from m in Enumerable.Range(0, modules.Count)
                   select KeyValuePair.Create(modules.GetKey(m), modules[m]);
        }
    }

    static class CacheExtensions
    {
        public static void Insert(this Cache cache, string key, object value, CacheDependency cacheDependency, CacheItemRemovedCallback onRemovedCallback)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            cache.Insert(key, value, cacheDependency,
                         Cache.NoAbsoluteExpiration, Cache.NoSlidingExpiration,
                         CacheItemPriority.Default, onRemovedCallback);
        }
    }

    static class VirtualPathProviderExtensions
    {
        public static Stream TryOpen(this VirtualPathProvider vpp, string virtualPath)
        {
            if (vpp == null) throw new ArgumentNullException(nameof(vpp));
            return vpp.FileExists(virtualPath) ? vpp.GetFile(virtualPath).Open() : null;
        }

        public static IEnumerable<string> ReadLines(this VirtualFile file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            using (var stream = file.Open())
            using (var reader = new StreamReader(stream))
            using (var e = reader.ReadLines())
                while (e.MoveNext())
                    yield return e.Current;
        }
    }

    static class RegexExtensions
    {
        public static T BindNum<T>(this Match match, Func<Group, Group, T> resultor)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));
            if (resultor == null) throw new ArgumentNullException(nameof(resultor));
            var groups = match.Groups;
            return resultor(groups[1], groups[2]);
        }
    }

    static class StreamExtensions
    {
        public static long? TryGetLength(this Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek) return null;
            try { return stream.Length; } catch (NotSupportedException) { return null; }
        }
    }

    static class TextReaderExtensions
    {
        public static IEnumerator<string> ReadLines(this TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            return ReadLinesImpl(reader);
        }

        static IEnumerator<string> ReadLinesImpl(this TextReader reader)
        {
            for (var line = reader.ReadLine(); line != null; line = reader.ReadLine())
                yield return line;
        }
    }

    static class CollectionExtensions
    {
        public static void AddRange<T>(this ICollection<T> collection, IEnumerable<T> source)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (source == null) throw new ArgumentNullException(nameof(source));

            foreach (var item in source)
                collection.Add(item);
        }
    }
}