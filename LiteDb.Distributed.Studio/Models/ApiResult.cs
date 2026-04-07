using System.Net;

namespace LiteDb.Distributed.Studio.Models
{
    public class ApiResult<T>
    {
        public bool Success { get; init; }
        public T? Data { get; init; }
        public string? ErrorMessage { get; init; }
        public HttpStatusCode? StatusCode { get; init; }
        public string? RawBody { get; init; }

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

