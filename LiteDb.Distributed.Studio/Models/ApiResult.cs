using System.Net;

namespace LiteDb.Distributed.Studio.Models
{
    public class ApiResult<T>
    {
        public bool Success { get; set; }
        public T? Data { get; set; }
        public string? ErrorMessage { get; set; }
        public HttpStatusCode? StatusCode { get; set; }
        public string? RawBody { get; set; }

        public static ApiResult<T> Ok(T? data, HttpStatusCode? statusCode = null)
        {
            return new ApiResult<T>
            {
                Success = true,
                Data = data,
                StatusCode = statusCode
            };
        }

        public static ApiResult<T> Fail(string errorMessage, HttpStatusCode? statusCode = null, string? rawBody = null)
        {
            return new ApiResult<T>
            {
                Success = false,
                ErrorMessage = errorMessage,
                StatusCode = statusCode,
                RawBody = rawBody
            };
        }
    }

}
