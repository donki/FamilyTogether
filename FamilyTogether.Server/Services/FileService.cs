using System.Text.Json;
using FamilyTogether.Server.Models;

namespace FamilyTogether.Server.Services;

public class FileService
{
    private readonly string _dataPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public FileService(IWebHostEnvironment environment)
    {
        _dataPath = Path.Combine(environment.ContentRootPath, "Data");
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        
        EnsureDataDirectoryExists();
        InitializeDataFiles();
    }

    private void EnsureDataDirectoryExists()
    {
        if (!Directory.Exists(_dataPath))
        {
            Directory.CreateDirectory(_dataPath);
        }
    }

    private void InitializeDataFiles()
    {
        var usersFile = Path.Combine(_dataPath, "users.json");
        var familiesFile = Path.Combine(_dataPath, "families.json");
        var locationsFile = Path.Combine(_dataPath, "locations.json");

        if (!File.Exists(usersFile))
        {
            var usersData = new { users = new List<User>() };
            File.WriteAllText(usersFile, JsonSerializer.Serialize(usersData, _jsonOptions));
        }

        if (!File.Exists(familiesFile))
        {
            var familiesData = new { families = new List<Family>() };
            File.WriteAllText(familiesFile, JsonSerializer.Serialize(familiesData, _jsonOptions));
        }

        if (!File.Exists(locationsFile))
        {
            var locationsData = new { locations = new List<LocationUpdate>() };
            File.WriteAllText(locationsFile, JsonSerializer.Serialize(locationsData, _jsonOptions));
        }
    }

    public async Task<List<User>> GetUsersAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var filePath = Path.Combine(_dataPath, "users.json");
            var json = await File.ReadAllTextAsync(filePath);
            var data = JsonSerializer.Deserialize<dynamic>(json);
            var usersJson = ((JsonElement)data).GetProperty("users").GetRawText();
            return JsonSerializer.Deserialize<List<User>>(usersJson, _jsonOptions) ?? new List<User>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveUsersAsync(List<User> users)
    {
        await _fileLock.WaitAsync();
        try
        {
            var filePath = Path.Combine(_dataPath, "users.json");
            var data = new { users };
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<List<Family>> GetFamiliesAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var filePath = Path.Combine(_dataPath, "families.json");
            var json = await File.ReadAllTextAsync(filePath);
            var data = JsonSerializer.Deserialize<dynamic>(json);
            var familiesJson = ((JsonElement)data).GetProperty("families").GetRawText();
            return JsonSerializer.Deserialize<List<Family>>(familiesJson, _jsonOptions) ?? new List<Family>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveFamiliesAsync(List<Family> families)
    {
        await _fileLock.WaitAsync();
        try
        {
            var filePath = Path.Combine(_dataPath, "families.json");
            var data = new { families };
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<List<LocationUpdate>> GetLocationsAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var filePath = Path.Combine(_dataPath, "locations.json");
            var json = await File.ReadAllTextAsync(filePath);
            var data = JsonSerializer.Deserialize<dynamic>(json);
            var locationsJson = ((JsonElement)data).GetProperty("locations").GetRawText();
            return JsonSerializer.Deserialize<List<LocationUpdate>>(locationsJson, _jsonOptions) ?? new List<LocationUpdate>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveLocationsAsync(List<LocationUpdate> locations)
    {
        await _fileLock.WaitAsync();
        try
        {
            var filePath = Path.Combine(_dataPath, "locations.json");
            var data = new { locations };
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}