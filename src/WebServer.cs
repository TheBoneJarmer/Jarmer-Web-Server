using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using AdvancedSockets.Http;
using Newtonsoft.Json;
using Jarmer.WebServer.Interfaces;
using AdvancedSockets;
using System.Text;
using AdvancedSockets.Http.Server;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Jarmer.WebServer
{
    public class WebServer
    {
        private HttpServer server;
        private string host;
        private int port;

        public string Host
        {
            get { return host; }
        }

        public int Port
        {
            get { return port; }
        }

        public WebServer(int port)
        {
            this.host = Dns.GetHostName();
            this.port = port;
        }
        public WebServer(string host, int port)
        {
            this.host = host;
            this.port = port;
        }

        public void Start()
        {
            var controllerTypes = GetControllerTypes();

            // Validate all controllers first
            foreach (var controllerType in controllerTypes)
            {
                var methodInfos = controllerType.GetMethods();

                foreach (var methodInfo in methodInfos)
                {
                    var attributes = methodInfo.GetCustomAttributes();
                    var httpAttrib = methodInfo.GetCustomAttribute<HttpAttribute>();
                    var contentTypeAttrib = methodInfo.GetCustomAttribute<ContentTypeAttribute>();
                    var parameters = methodInfo.GetParameters();

                    // If the method contains neither an HttpAttribute and returns no action result
                    // We'll assume it is not meant to be an action at all
                    if (httpAttrib == null && !ReturnsActionResult(methodInfo))
                    {
                        continue;
                    }

                    // Actions need to return an object of type ActionResult and contain an http attribute
                    // That is how we know the method is meant as an action
                    // If either one of them is missing or invalid, the method is considered broken
                    // Also, the method has to be public
                    if (!ReturnsActionResult(methodInfo) && httpAttrib != null)
                    {
                        throw new Exception($"Method {methodInfo.Name} in controller {controllerType.Name} has a {httpAttrib.GetType().Name} attribute defined but returns no ActionResult object");
                    }
                    if (ReturnsActionResult(methodInfo) && httpAttrib == null)
                    {
                        throw new Exception($"Method {methodInfo.Name} in controller {controllerType.Name} returns an ActionResult object but has no Http attribute defined");
                    }
                    if (!methodInfo.IsPublic && httpAttrib != null)
                    {
                        throw new Exception($"Action {methodInfo.Name} in controller {controllerType.Name} is not public");
                    }
                }
            }

            // If validation succeeded, start the server
            server = new HttpServer(host, port);
            server.OnRequest += Server_OnRequest;
            server.OnException += Server_OnException;
            server.OnHttpError += Server_OnHttpError;
            server.OnRequestStart += Server_OnRequestStart;
            server.OnRequestEnd += Server_OnRequestEnd;
            server.Start();
        }

        /* SERVER EVENTS */
        private void Server_OnException(Exception ex)
        {
            OnException?.Invoke(ex);
        }

        private void Server_OnHttpError(HttpStatusCode status, string error, HttpRequest request, HttpResponse response, HttpConnectionInfo info)
        {
            if (OnHttpError != null)
            {
                HandleResult(response, status, OnHttpError(request, error));
            }
            else
            {
                HandleResult(response, status, new TextResult(error));
            }
        }
        private void Server_OnRequestStart(HttpRequest request, HttpConnectionInfo connectionInfo)
        {
            OnRequestStart?.Invoke(request, connectionInfo);
        }

        private void Server_OnRequestEnd(HttpRequest request, HttpResponse response, HttpConnectionInfo connectionInfo)
        {
            OnRequestEnd?.Invoke(request, response, connectionInfo);
        }

        private void Server_OnRequest(HttpRequest request, HttpResponse response, HttpConnectionInfo info)
        {
            try
            {
                // If the request is for a media file like a css, js or image file, give that priority
                // If no such content was found we will just assume the user requests a controller action
                if (File.Exists(ContentPath(request.Path)))
                {
                    response.SendFile(ContentPath(request.Path));
                }
                else
                {
                    HandleController(request, info, response);
                }
            }
            catch (HttpException ex)
            {
                Server_OnHttpError(ex.Status, ex.Message, request, response, info);
            }
            catch (Exception ex)
            {
                Server_OnHttpError(HttpStatusCode.InternalServerError, "Something went wrong", request, response, info);
                Server_OnException(ex);
            }
        }

        /* SERVER LOGIC */
        private void HandleController(HttpRequest request, HttpConnectionInfo info, HttpResponse response)
        {
            var controllerTypes = GetControllerTypes();

            // First gather all methods which match the request's method and path
            foreach (var controllerType in controllerTypes)
            {
                var actions = GetControllerActions(controllerType);

                foreach (var action in actions)
                {
                    var attributes = action.GetCustomAttributes();
                    var httpAttrib = action.GetCustomAttribute<HttpAttribute>();

                    // Compare the http attrib's values with our request
                    if (httpAttrib.Method != request.Method || httpAttrib.Path != request.Path)
                    {
                        continue;
                    }

                    // If the method has a http attribute and the value matches the request, handle remaining attributes and the action
                    foreach (var attrib in attributes)
                    {
                        Type attribType = attrib.GetType();
                        
                        if (attribType.BaseType == typeof(CustomWebAttribute))
                        {
                            var customAttrib = (CustomWebAttribute)attrib;
                            var result = customAttrib.Handle(request);

                            if (result != null)
                            {
                                HandleResult(response, HttpStatusCode.OK, result);
                                return;
                            }
                        }
                    }

                    HandleAction(request, info, response, controllerType, action);
                    return;
                }
            }

            throw new HttpException(HttpStatusCode.NotFound, $"Action or content {request.Method.ToString().ToUpper()} {request.Path} not found");
        }

        private void HandleAction(HttpRequest request, HttpConnectionInfo info, HttpResponse response, Type controllerType, MethodInfo methodInfo)
        {
            var controller = (IController)Activator.CreateInstance(controllerType);
            var arguments = GenerateActionArgs(request, methodInfo);

            // Set required parameters
            controller.Request = request;
            controller.ConnectionInfo = info;
            controller.Cookies = new HttpCookies();

            // Execute the method
            try
            {
                var result = methodInfo.Invoke(controller, arguments);

                if (result == null)
                {
                    throw new Exception($"Result cannot be null");
                }
                else
                {
                    var actionResult = (ActionResult)result;

                    // Set cookies and session variables
                    foreach (var entry in controller.Cookies.ToList())
                    {
                        response.Cookies.Add(entry.Key, entry.Value);
                    }

                    // And finally handle the result
                    HandleResult(response, HttpStatusCode.OK, actionResult);
                }
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }
        private object[] GenerateActionArgs(HttpRequest request, MethodInfo methodInfo)
        {
            var contentTypeAttrib = methodInfo.GetCustomAttribute<ContentTypeAttribute>();
            var contentType = "";
            var parameters = methodInfo.GetParameters();
            var args = new object[parameters.Length];

            // Check if the method has arguments and the body is empty, this is to prevent an exception
            if (request.Body != null && request.Headers.ContentType == null)
            {
                throw new HttpException(HttpStatusCode.UnsupportedMediaType, "No 'Content-Type' header was present in the HTTP request");
            }

            // Update args with defaults
            for (var i=0; i<args.Length; i++)
            {
                if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
            }

            // If the attribute was set, use its value to define the expected content type
            // Otheriwse use the header
            if (contentTypeAttrib != null)
            {
                contentType = contentTypeAttrib.ContentType;

                // Compare the request's content type to the one required by the action
                if (!request.Headers.ContentType.StartsWith(contentType))
                {
                    throw new HttpException(HttpStatusCode.UnsupportedMediaType, $"Invalid content type provided, expected '{contentType}'");
                }
            }
            else
            {
                contentType = request.Headers.ContentType;
            }

            // Empty the contenttype if the body is empty
            if (request.Body == null || (request.Body.Data == null && request.Body.Files == null && request.Body.KeyValues == null))
            {
                contentType = "";
            }

            // Start by fetching args from the query
            if (request.Query != null)
            {
                UpdateArgsWithQuery(request.Query.ToDictionary(), parameters, ref args);
            }

            // Now, depending on the content type, generate arguments for the method 
            if (contentType != null && contentType.StartsWith("application/x-www-form-urlencoded"))
            {
                if (request.Body.KeyValues == null)
                {
                    throw new HttpException("Request body contains no keyvalue pairs");
                }

                UpdateArgsWithQuery(request.Body.KeyValues, parameters, ref args);
            }
            else if (contentType != null && contentType.StartsWith("application/json"))
            {
                // If there is just 1 parameter we expect it to have the model's datatype
                if (parameters.Length == 1)
                {
                    // Added an additional try catch here because invalid json should not "crash" the request by triggering a 500
                    // but rather make the input invalid with a 400 as the error message that the JsonConvert.DeserializeObject produces
                    // tells exactly at which character the json is broken.
                    try
                    {
                        args[0] = JsonConvert.DeserializeObject(request.CharSet.GetString(request.Body.Data), parameters[0].ParameterType);
                    }
                    catch (JsonReaderException ex)
                    {
                        throw new HttpException(HttpStatusCode.BadRequest, ex.Message);
                    }
                }

                // If not, we expect one of the parameters to contain a FromBody attribute
                // Otherwise we can't know which parameter is supposed to be used as model
                // In that case, the argument will be null
                if (parameters.Length > 1)
                {
                    for (var i=0; i<parameters.Length; i++)
                    {
                        var param = parameters[i];
                        var attrib = param.GetCustomAttribute<FromBodyAttribute>();

                        if (attrib == null)
                        {
                            continue;
                        }

                        if (param.ParameterType == typeof(string))
                        {
                            args[i] = request.CharSet.GetString(request.Body.Data);
                            continue;
                        }
                        if (param.ParameterType == typeof(byte[]))
                        {
                            args[i] = request.Body.Data;
                            continue;
                        }

                        try
                        {
                            args[i] = JsonConvert.DeserializeObject(request.CharSet.GetString(request.Body.Data), parameters[i].ParameterType);
                        }
                        catch (JsonReaderException ex)
                        {
                            throw new HttpException(HttpStatusCode.BadRequest, ex.Message);
                        }
                    }
                }
            }
            else if (contentType != null && contentType.StartsWith("multipart/form-data"))
            {
                args = new object[parameters.Length];

                // First match all normal fields
                if (request.Body.KeyValues != null)
                {
                    UpdateArgsWithQuery(request.Body.KeyValues, parameters, ref args);
                }

                // Then match all remaining fields
                for (var i=0; i<parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    var attrib = parameter.GetCustomAttribute<FromBodyAttribute>();
                    var file = request.Body.Files.FirstOrDefault(x => x.Key == parameter.Name);

                    // Continue only when a match was found
                    // Or when there is only 1 parameter
                    if (parameters.Length == 1)
                    {
                        file = request.Body.Files.FirstOrDefault();
                    }
                    if (file == null)
                    {
                        continue;
                    }

                    // The webserver will convert the file or its contents depending on the parameter type
                    if (parameter.ParameterType == typeof(HttpFile))
                    {
                        args[i] = file;
                    }
                    if (parameter.ParameterType == typeof(byte[]))
                    {
                        args[i] = file.Data;
                    }

                    // The webserver also supports an array of httpfiles, which is exactly the same type as the request.Body.Files property
                    // In this case we just assing the argument without conversion
                    if (parameter.ParameterType == typeof(HttpFile[]))
                    {
                        args[i] = request.Body.Files;
                    }

                    // String conversion is a special case because we don't know the encoding unless it was set as part of the content type
                    // Therefore we will assume the encoding was default unless specified otherwise
                    if (parameter.ParameterType == typeof(string))
                    {
                        args[i] = file.CharSet.GetString(file.Data);
                    }

                    // The webserver will also convert json if the content type is json and the frombody attrib is set
                    if (attrib != null && file.ContentType.StartsWith("application/json"))
                    {
                        try
                        {
                            args[i] = JsonConvert.DeserializeObject(file.CharSet.GetString(file.Data), parameters[i].ParameterType);
                        }
                        catch (JsonReaderException ex)
                        {
                            throw new HttpException(HttpStatusCode.BadRequest, ex.Message);
                        }
                    }
                }
            }
            else
            {
                // If it so happens the user uses neither one of the 3 support content types, we're just going to assume the content is special and provide it to each parameter
                // In this case our conversion options are limited to either a byte array or a string
                // If the parameter type holds any other value we are just going to ignore it and leave it up to the user to convert it
                if (request.Body != null)
                {
                    for (var i=0; i<parameters.Length; i++)
                    {
                        var parameter = parameters[i];

                        if (parameter.ParameterType == typeof(byte[]))
                        {
                            args[i] = request.Body.Data;
                        }
                        if (parameter.ParameterType == typeof(string))
                        {
                            args[i] = request.CharSet.GetString(request.Body.Data);
                        }
                    }
                }
            }

            return args;
        }
        private void UpdateArgsWithQuery(Dictionary<string, string> query, ParameterInfo[] parameters, ref object[] args)
        {
            for (var i=0; i<parameters.Length; i++)
            {
                var paramInfo = parameters[i];
                var attrib = paramInfo.GetCustomAttribute<FromBodyAttribute>();
                var name = paramInfo.Name.ToLower();

                // If the from body attribute is set the user specifically requested the input coming from the body
                if (attrib != null)
                {
                    continue;
                }

                for (var j=0; j<query.Count; j++)
                {
                    var key = query.Keys.ToArray()[j].ToLower();
                    var value = query.Values.ToArray()[j];

                    if (name == key)
                    {
                        args[i] = Convert.ChangeType(HttpUtils.ConvertUrlEncoding(value), paramInfo.ParameterType);
                    }
                }
            }
        }
        private void HandleResult(HttpResponse response, HttpStatusCode statusCode, ActionResult result)
        {
            response.StatusCode = statusCode;
            response.Headers.Server = "Reapenshaw Web Server";
            
            if (result.GetType() == typeof(JsonResult))
            {
                var jsonResult = (JsonResult)result;
                var json = JsonConvert.SerializeObject(jsonResult.Body);

                response.Headers.ContentType = "application/json";
                response.Body = Encoding.ASCII.GetBytes(json);
            }
            if (result.GetType() == typeof(TextResult))
            {
                var textResult = (TextResult)result;

                response.Headers.ContentType = "text/plain; charset=utf-8";
                response.Body = Encoding.ASCII.GetBytes(textResult.Text);
            }
            if (result.GetType() == typeof(RedirectResult))
            {
                var redirectResult = (RedirectResult)result;

                response.Headers.Location = redirectResult.Url;
                response.StatusCode = HttpStatusCode.Redirect;
            }

            OnSend?.Invoke(response);
            response.Send();
        }

        private string ContentPath(string path)
        {
            var wwwPath = $"www{path}";

            if (wwwPath.EndsWith("/")) {
                wwwPath += "index.html";
            }

            return wwwPath;
        }
        private bool ReturnsActionResult(MethodInfo methodInfo)
        {
            var returnType = methodInfo.ReturnType;
            var returnBaseType = methodInfo.ReturnType != null ? methodInfo.ReturnType.BaseType : null;
            
            return returnBaseType == typeof(ActionResult) || returnType == typeof(ActionResult);
        }
        private Type[] GetControllerTypes()
        {
            var assembly = Assembly.GetEntryAssembly();
            var types = assembly.GetTypes();
            var controllerTypes = types.Where(x => x.IsClass && x.GetInterface("IController") != null);

            return controllerTypes.ToArray();
        }
        private MethodInfo[] GetControllerActions(Type controllerType)
        {
            var methods = controllerType.GetMethods();
            var result = new List<MethodInfo>();

            foreach (var method in methods)
            {
                var httpAttrib = method.GetCustomAttribute<HttpAttribute>();
                
                if (httpAttrib == null)
                {
                    continue;
                }
                if (!ReturnsActionResult(method))
                {
                    continue;
                }

                result.Add(method);
            }

            return result.ToArray();
        }

        private string HexToASCII(string input)
        {
            var output = input;

            for (var j=255; j>0; j--)
            {
                var hex = j.ToString("X");
                var src = $"%{hex}";
                var dest = ((char)j).ToString();

                output = output.Replace(src, dest);
            }

            return output;
        }

        /* EVENTS */
        public delegate ActionResult OnHttpErrorDelegate(HttpRequest request, string error);
        public delegate void OnExceptionDelegate(Exception exception);
        public delegate void OnRequestStartDelegate(HttpRequest request, HttpConnectionInfo connectionInfo);
        public delegate void OnRequestEndDelegate(HttpRequest request, HttpResponse response, HttpConnectionInfo connectionInfo);
        public delegate void OnSendDelegate(HttpResponse response);

        public event OnHttpErrorDelegate OnHttpError;
        public event OnExceptionDelegate OnException;
        public event OnRequestStartDelegate OnRequestStart;
        public event OnRequestEndDelegate OnRequestEnd;
        public event OnSendDelegate OnSend;
    }
}