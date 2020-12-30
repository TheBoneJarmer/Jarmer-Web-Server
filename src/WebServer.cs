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

            server.OnRequestStart += OnRequestStart;
            server.OnRequestEnd += OnRequestEnd;
        }

        public void Run()
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
                    if (contentTypeAttrib != null && httpAttrib.Method == HttpMethod.Get)
                    {
                        throw new Exception($"Action {methodInfo.Name} in controller {controllerType.Name} has a ContentType attribute defined but the Http attribute's Method property is set to HttpMethod.Get, which is an illegal combination since GET requests do not have a body");
                    }
                    if (contentTypeAttrib != null && contentTypeAttrib.ContentType == "application/json" && methodInfo.GetParameters().Length != 1)
                    {
                        throw new Exception($"Action {methodInfo.Name} in controller {controllerType.Name} has too many parameters. If the action is configured to accept json the method should have only 1 parameter whose type represents an object which can be mapped to the JSON object");
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
            var arguments = new object[0];
            var parameters = methodInfo.GetParameters();

            // If the method is GET, try matching the parameters from the query
            // Otheriwse, in case of POST, PUT, DELETE and what not try getting it from the body
            if (request.Method == HttpMethod.Get)
            {
                arguments = GenerateActionArgs(request.Query.ToDictionary(), methodInfo);
            }
            else
            {
                arguments = GenerateActionArgs(request, methodInfo);
            }           

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
        private object[] GenerateActionArgs(Dictionary<string, string> query, MethodInfo methodInfo)
        {
            var parameters = methodInfo.GetParameters();
            var arguments = new object[parameters.Length];

            if (query.Count < parameters.Count(x => !x.HasDefaultValue))
            {
                throw new HttpException(HttpStatusCode.BadRequest, "Not enough arguments provided");
            }

            for (var i=0; i<parameters.Length; i++)
            {
                var paramInfo = parameters[i];
                var key = paramInfo.Name.ToLower();

                if (paramInfo.HasDefaultValue)
                {
                    arguments[i] = paramInfo.DefaultValue;
                }
                if (query.ContainsKey(key))
                {
                    arguments[i] = Convert.ChangeType(HttpUtils.ConvertUrlEncoding(query[key]), paramInfo.ParameterType);
                }
            }

            return arguments;
        }
        private object[] GenerateActionArgs(HttpRequest request, MethodInfo methodInfo)
        {
            var contentTypeAttrib = methodInfo.GetCustomAttribute<ContentTypeAttribute>();
            var contentType = "application/x-www-form-urlencoded";
            var parameters = methodInfo.GetParameters();
            var arguments = new object[0];

            // Check if the method has arguments and the body is empty, this is to prevent an exception
            if (parameters.Length != 0 && request.Body.Data == null && request.Body.KeyValues == null && request.Body.Files == null)
            {
                throw new HttpException(HttpStatusCode.BadRequest, "Request contains no body or uploaded files while the action requires input");
            }

            // If the default content type is overriden with an attribute, use the value of the attribute
            if (contentTypeAttrib != null)
            {
                contentType = contentTypeAttrib.ContentType;
            }

            // Compare the request's content type to the one required by the action
            if (request.Headers.ContentType == null)
            {
                throw new HttpException(HttpStatusCode.UnsupportedMediaType, "No 'Content-Type' header was present in the HTTP request");
            }
            if (!request.Headers.ContentType.StartsWith(contentType))
            {
                throw new HttpException(HttpStatusCode.UnsupportedMediaType, $"Invalid content type provided, expected '{contentType}'");
            }

            // Now, depending on the content type, generate arguments for the method 
            if (contentType == "application/x-www-form-urlencoded")
            {
                arguments = GenerateActionArgs(request.Body.KeyValues, methodInfo);
            }
            if (contentType == "application/json")
            {
                // Added an additional try catch here because invalid json should not "crash" the request by triggering a 500
                // but rather make the input invalid with a 400 as the error message that the JsonConvert.DeserializeObject produces
                // tells exactly at which character the json is broken
                //
                // This information is much more relevant to the user than the "Something went wrong" message since something did went wrong
                // but not because of an unkown runtime error, which is what the 500 status is actually saying
                try
                {
                    arguments = new object[1];
                    arguments[0] = JsonConvert.DeserializeObject(Encoding.ASCII.GetString(request.Body.Data), parameters[0].ParameterType);
                }
                catch (JsonReaderException ex)
                {
                    throw new HttpException(HttpStatusCode.BadRequest, ex.Message);
                }
            }
            if (contentType == "multipart/form-data")
            {
                arguments = new object[parameters.Length];

                // First match all normal fields
                if (request.Body.KeyValues != null)
                {
                    arguments = GenerateActionArgs(request.Body.KeyValues, methodInfo);
                }

                // Than match the http files
                for (var i=0; i<parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    var file = request.Body.Files.FirstOrDefault(x => x.Key == parameter.Name);

                    if (file != null)
                    {
                        arguments[i] = file;
                    }
                }
            }

            return arguments;
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

        public event OnHttpErrorDelegate OnHttpError;
        public event OnExceptionDelegate OnException;

        public event HttpServer.OnRequestStartDelegate OnRequestStart;
        public event HttpServer.OnRequestEndDelegate OnRequestEnd;
    }
}