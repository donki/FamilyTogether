using System.Text.Json;
using FamilyTogether.App.Models;

namespace FamilyTogether.App.Services;

public class OfflineStorageService
{
    private readonly string _dataDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    private OfflineData _offlineData;
    private readonly object _lockObject = new object();
    
    public event EventHandler<int>? PendingDataCountChanged;
    
    public OfflineStorageService()
    {
        _dataDirectory = Path.Combine(FileSystem.AppDataDirectory, "offline");
        Directory.CreateDirectory(_dataDirectory);
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        
        _offlineData = LoadOfflineData();
    }
    
    public void StoreLocationUpdate(LocationUpdate locationUpdate)
    {
        lock (_lockObject)
        {
            // Remove any existing update for the same user to avoid duplicates
            _offlineData.PendingLocationUpdates.RemoveAll(l => l.UserId == locationUpdate.UserId);
            
            // Add the new update
            _offlineData.PendingLocationUpdates.Add(locationUpdate);
            
            // Keep only the last 100 location updates to prevent excessive storage
            if (_offlineData.PendingLocationUpdates.Count > 100)
            {
                _offlineData.PendingLocationUpdates = _offlineData.PendingLocationUpdates
                    .OrderByDescending(l => l.Timestamp)
                    .Take(100)
                    .ToList();
            }
            
            SaveOfflineData();
            NotifyPendingDataChanged();
        }
        
        System.Diagnostics.Debug.WriteLine($"Stored location update offline for user {locationUpdate.UserId}");
    }
    
    public void StoreApiRequest(string method, string endpoint, object? data = null)
    {
        lock (_lockObject)
        {
            var request = new ApiRequest
            {
                Method = method,
                Endpoint = endpoint,
                JsonData = data != null ? JsonSerializer.Serialize(data, _jsonOptions) : null,
                CreatedAt = DateTime.UtcNow
            };
            
            _offlineData.PendingRequests.Add(request);
            
            // Remove expired requests (older than 24 hours)
            _offlineData.PendingRequests.RemoveAll(r => r.IsExpired);
            
            // Keep only the last 50 requests to prevent excessive storage
            if (_offlineData.PendingRequests.Count > 50)
            {
                _offlineData.PendingRequests = _offlineData.PendingRequests
                    .OrderByDescending(r => r.CreatedAt)
                    .Take(50)
                    .ToList();
            }
            
            SaveOfflineData();
            NotifyPendingDataChanged();
        }
        
        System.Diagnostics.Debug.WriteLine($"Stored API request offline: {method} {endpoint}");
    }
    
    public List<LocationUpdate> GetPendingLocationUpdates()
    {
        lock (_lockObject)
        {
            return new List<LocationUpdate>(_offlineData.PendingLocationUpdates);
        }
    }
    
    public List<ApiRequest> GetPendingApiRequests()
    {
        lock (_lockObject)
        {
            return new List<ApiRequest>(_offlineData.PendingRequests);
        }
    }
    
    public void RemoveLocationUpdate(LocationUpdate locationUpdate)
    {
        lock (_lockObject)
        {
            _offlineData.PendingLocationUpdates.RemoveAll(l => 
                l.UserId == locationUpdate.UserId && 
                l.Timestamp == locationUpdate.Timestamp);
            
            SaveOfflineData();
            NotifyPendingDataChanged();
        }
    }
    
    public void RemoveApiRequest(string requestId)
    {
        lock (_lockObject)
        {
            _offlineData.PendingRequests.RemoveAll(r => r.Id == requestId);
            SaveOfflineData();
            NotifyPendingDataChanged();
        }
    }
    
    public void ClearAllPendingData()
    {
        lock (_lockObject)
        {
            _offlineData.PendingLocationUpdates.Clear();
            _offlineData.PendingRequests.Clear();
            SaveOfflineData();
            NotifyPendingDataChanged();
        }
        
        System.Diagnostics.Debug.WriteLine("Cleared all pending offline data");
    }
    
    public bool HasPendingData()
    {
        lock (_lockObject)
        {
            return _offlineData.HasPendingData;
        }
    }
    
    public int GetPendingDataCount()
    {
        lock (_lockObject)
        {
            return _offlineData.PendingLocationUpdates.Count + _offlineData.PendingRequests.Count;
        }
    }
    
    public DateTime GetLastSyncTime()
    {
        lock (_lockObject)
        {
            return _offlineData.LastSync;
        }
    }
    
    public void UpdateLastSyncTime()
    {
        lock (_lockObject)
        {
            _offlineData.LastSync = DateTime.UtcNow;
            SaveOfflineData();
        }
    }
    
    public void CleanupExpiredData()
    {
        lock (_lockObject)
        {
            var initialCount = _offlineData.PendingRequests.Count;
            
            // Remove expired requests
            _offlineData.PendingRequests.RemoveAll(r => r.IsExpired);
            
            // Remove very old location updates (older than 6 hours)
            var cutoffTime = DateTime.UtcNow.AddHours(-6);
            _offlineData.PendingLocationUpdates.RemoveAll(l => l.Timestamp < cutoffTime);
            
            var finalCount = _offlineData.PendingRequests.Count;
            
            if (initialCount != finalCount)
            {
                SaveOfflineData();
                NotifyPendingDataChanged();
                System.Diagnostics.Debug.WriteLine($"Cleaned up {initialCount - finalCount} expired offline items");
            }
        }
    }
    
    private OfflineData LoadOfflineData()
    {
        try
        {
            var filePath = Path.Combine(_dataDirectory, "offline_data.json");
            
            if (!File.Exists(filePath))
            {
                return new OfflineData { LastSync = DateTime.UtcNow };
            }
            
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<OfflineData>(json, _jsonOptions);
            
            if (data == null)
            {
                return new OfflineData { LastSync = DateTime.UtcNow };
            }
            
            // Clean up expired data on load
            data.PendingRequests.RemoveAll(r => r.IsExpired);
            
            System.Diagnostics.Debug.WriteLine($"Loaded offline data: {data.PendingLocationUpdates.Count} locations, {data.PendingRequests.Count} requests");
            
            return data;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading offline data: {ex.Message}");
            return new OfflineData { LastSync = DateTime.UtcNow };
        }
    }
    
    private void SaveOfflineData()
    {
        try
        {
            var filePath = Path.Combine(_dataDirectory, "offline_data.json");
            var json = JsonSerializer.Serialize(_offlineData, _jsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving offline data: {ex.Message}");
        }
    }
    
    private void NotifyPendingDataChanged()
    {
        var count = _offlineData.PendingLocationUpdates.Count + _offlineData.PendingRequests.Count;
        PendingDataCountChanged?.Invoke(this, count);
    }
    
    public OfflineData GetOfflineDataSnapshot()
    {
        lock (_lockObject)
        {
            return new OfflineData
            {
                PendingLocationUpdates = new List<LocationUpdate>(_offlineData.PendingLocationUpdates),
                PendingRequests = new List<ApiRequest>(_offlineData.PendingRequests),
                LastSync = _offlineData.LastSync
            };
        }
    }
}