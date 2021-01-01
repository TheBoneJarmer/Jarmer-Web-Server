using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using AdvancedSockets.Http;
using Newtonsoft.Json;
using Jarmer.WebServer.Interfaces;
using AdvancedSockets;
using System.Text;
using AdvancedSockets.Http.Server;

namespace Jarmer.WebServer
{
    public class WebServer
    {
        private HttpServer server;

        public WebServer(string host, int port)
         {
            server = new HttpServer(host, port);
            server.OnRequest += Server_OnRequest;
            server.OnException += Server_OnException;
            server.OnHttpError += Server_OnHttpError;
            server.OnRequestStart += Server_OnRequestStart;
            server.OnRequestEnd += Server_OnRequestEnd;
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

        private void Server_OnRequestEnd(HttpResponse response, HttpConnectionInfo connectionInfo)
        {
            OnRequestEnd?.Invoke(response, connectionInfo);
        }

        private void Server_OnRequest(HttpRequest request, HttpResponse response, HttpConnectionInfo info)
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
                throw ex.InnerException;
            }
        }
        private object[] GenerateActionArgs(HttpRequest request, MethodInfo methodInfo)
        {
            var contentTypeAttrib = methodInfo.GetCustomAttribute<ContentTypeAttribute>();
            var contentType = "";
            var parameters = methodInfo.GetParameters();
            var args = new object[parameters.Length];

            // Check if the method has arguments and the body is empty, this is to prevent an exception
            if (parameters.Length != 0 && request.Query == null && request.Body.Data == null && request.Body.KeyValues == null && request.Body.Files == null)
            {
                throw new HttpException(HttpStatusCode.BadRequest, "Request contains no input while the action requires it");
            }

            // If the attribute was set, use its value to define the expected content type
            if (contentTypeAttrib != null)
            {
                contentType = contentTypeAttrib.ContentType;
            }

            // Compare the request's content type to the one required by the action
            if (!string.IsNullOrEmpty(contentType))
            {
                if (request.Headers.ContentType == null)
                {
                    throw new HttpException(HttpStatusCode.UnsupportedMediaType, "No 'Content-Type' header was present in the HTTP request");
                }
                if (!request.Headers.ContentType.StartsWith(contentType))
                {
                    throw new HttpException(HttpStatusCode.UnsupportedMediaType, $"Invalid content type provided, expected '{contentType}'");
                }
            }

            // Start by fetching args from the query
            if (request.Query != null)
            {
                UpdateArgsWithQuery(request.Query.ToDictionary(), parameters, ref args);
            }

            // Now, depending on the content type, generate arguments for the method 
            if (contentType == "application/x-www-form-urlencoded")
            {
                if (request.Body.KeyValues == null)
                {
                    throw new HttpException("Request body contains no keyvalue pairs");
                }

                UpdateArgsWithQuery(request.Body.KeyValues, parameters, ref args);
            }
            if (contentType == "application/json")
            {
                // If there is just 1 parameter we expect it to have the model's datatype
                if (parameters.Length == 1)
                {
                    // Added an additional try catch here because invalid json should not "crash" the request by triggering a 500
                    // but rather make the input invalid with a 400 as the error message that the JsonConvert.DeserializeObject produces
                    // tells exactly at which character the json is broken.
                    try
                    {
                        args[0] = JsonConvert.DeserializeObject(Encoding.ASCII.GetString(request.Body.Data), parameters[0].ParameterType);
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
            if (contentType == "multipart/form-data")
            {
                args = new object[parameters.Length];

                // First match all normal fields
                if (request.Body.KeyValues != null)
                {
                    UpdateArgsWithQuery(request.Body.KeyValues, parameters, ref args);
                }

                // If there is just 1 param and it is an array of HttpFile, assign the body's Files property to it
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(HttpFile[]))
                {
                    args[0] = request.Body.Files.ToArray();
                }
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(List<HttpFile>))
                {
                    args[0] = request.Body.Files.ToList();
                }

                // If not, match the file keys to the parameters
                for (var i=0; i<parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    var attrib = parameter.GetCustomAttribute<FromBodyAttribute>();
                    var file = request.Body.Files.FirstOrDefault(x => x.Key == parameter.Name);

                    // Continue only when a match was found
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

                    // String conversion is a special case because we don't know the encoding unless it was set as part of the content type
                    // Therefore we will assume the encoding was default unless specified otherwise
                    if (parameter.ParameterType == typeof(string))
                    {
                        args[i] = file.CharSet.GetString(file.Data.ToArray());
                    }

                    // The webserver will also convert json if the content type is json and the frombody attrib is set
                    if (attrib != null && file.ContentType.StartsWith("application/json"))
                    {
                        try
                        {
                            args[i] = JsonConvert.DeserializeObject(file.CharSet.GetString(request.Body.Data), parameters[i].ParameterType);
                        }
                        catch (JsonReaderException ex)
                        {
                            throw new HttpException(HttpStatusCode.BadRequest, ex.Message);
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
                var key = paramInfo.Name.ToLower();

                if (paramInfo.HasDefaultValue)
                {
                    args[i] = paramInfo.DefaultValue;
                }
                if (query.ContainsKey(key))
                {
                    args[i] = Convert.ChangeType(HttpUtils.ConvertUrlEncoding(query[key]), paramInfo.ParameterType);
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
        public delegate void OnRequestEndDelegate(HttpResponse response, HttpConnectionInfo connectionInfo);

        public event OnHttpErrorDelegate OnHttpError;
        public event OnExceptionDelegate OnException;
        public event OnRequestStartDelegate OnRequestStart;
        public event OnRequestEndDelegate OnRequestEnd;
    }
}