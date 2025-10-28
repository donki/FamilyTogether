namespace FamilyTogether.Functions.Models;

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public T? Data { get; set; }
    
    public static ApiResponse<T> SuccessResult(T data, string message = "Success")
    {
        return new ApiResponse<T>
        {
            Success = true,
            Message = message,
            Data = data
        };
    }
    
    public static ApiResponse<T> ErrorResult(string message)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Message = message,
            Data = default
        };
    }
}

public class ApiResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    
    public static ApiResponse CreateSuccess(string message = "Success")
    {
        return new ApiResponse
        {
            Success = true,
            Message = message
        };
    }
    
    public static ApiResponse CreateError(string message)
    {
        return new ApiResponse
        {
            Success = false,
            Message = message
        };
    }
}
