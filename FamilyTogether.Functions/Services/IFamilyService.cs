using FamilyTogether.Functions.Models;

namespace FamilyTogether.Functions.Services;

public interface IFamilyService
{
    Task<Family> CreateFamilyAsync(int userId, CreateFamilyRequest request);
    Task<Family?> JoinFamilyAsync(int userId, JoinFamilyRequest request);
    Task<List<Family>> GetUserFamiliesAsync(int userId);
    Task<bool> LeaveFamilyAsync(int userId, int familyId);
    Task<bool> RemoveMemberAsync(int adminUserId, int familyId, int memberUserId);
}
