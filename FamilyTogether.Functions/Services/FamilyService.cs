using FamilyTogether.Functions.Models;

namespace FamilyTogether.Functions.Services;

public class FamilyService : IFamilyService
{
    private readonly IDataService _dataService;

    public FamilyService(IDataService dataService)
    {
        _dataService = dataService;
    }

    public async Task<Family> CreateFamilyAsync(int userId, CreateFamilyRequest request)
    {
        var user = await _dataService.GetUserByIdAsync(userId);
        if (user == null)
        {
            throw new ArgumentException("User not found");
        }

        var family = new Family
        {
            Name = request.Name,
            CreatedBy = userId,
            Members = new List<FamilyMember>
            {
                new FamilyMember
                {
                    UserId = userId,
                    Name = user.Name,
                    IsAdmin = true,
                    Status = "active",
                    JoinedAt = DateTime.UtcNow
                }
            }
        };

        return await _dataService.CreateFamilyAsync(family);
    }

    public async Task<Family?> JoinFamilyAsync(int userId, JoinFamilyRequest request)
    {
        var user = await _dataService.GetUserByIdAsync(userId);
        if (user == null)
        {
            return null;
        }

        var family = await _dataService.GetFamilyByGuidAsync(request.FamilyGuid);
        if (family == null)
        {
            return null;
        }

        // Check if user is already a member
        var existingMember = family.Members.FirstOrDefault(m => m.UserId == userId);
        if (existingMember != null)
        {
            // If pending, activate the membership
            if (existingMember.Status == "pending")
            {
                existingMember.Status = "active";
                existingMember.JoinedAt = DateTime.UtcNow;
                return await _dataService.UpdateFamilyAsync(family);
            }
            
            // Already active member
            return family;
        }

        // Add new member
        family.Members.Add(new FamilyMember
        {
            UserId = userId,
            Name = user.Name,
            IsAdmin = false,
            Status = "active",
            JoinedAt = DateTime.UtcNow
        });

        return await _dataService.UpdateFamilyAsync(family);
    }

    public async Task<List<Family>> GetUserFamiliesAsync(int userId)
    {
        return await _dataService.GetFamiliesByUserIdAsync(userId);
    }

    public async Task<bool> LeaveFamilyAsync(int userId, int familyId)
    {
        var family = await _dataService.GetFamilyByIdAsync(familyId);
        if (family == null)
        {
            return false;
        }

        var member = family.Members.FirstOrDefault(m => m.UserId == userId);
        if (member == null)
        {
            return false;
        }

        // Remove the member
        family.Members.Remove(member);

        // If this was the last member, we could delete the family
        // For now, we'll keep empty families
        
        await _dataService.UpdateFamilyAsync(family);
        return true;
    }

    public async Task<bool> RemoveMemberAsync(int adminUserId, int familyId, int memberUserId)
    {
        var family = await _dataService.GetFamilyByIdAsync(familyId);
        if (family == null)
        {
            return false;
        }

        // Check if the requesting user is an admin
        var adminMember = family.Members.FirstOrDefault(m => m.UserId == adminUserId && m.IsAdmin);
        if (adminMember == null)
        {
            return false;
        }

        // Find the member to remove
        var memberToRemove = family.Members.FirstOrDefault(m => m.UserId == memberUserId);
        if (memberToRemove == null)
        {
            return false;
        }

        // Don't allow removing the family creator unless there's another admin
        if (family.CreatedBy == memberUserId)
        {
            var otherAdmins = family.Members.Count(m => m.IsAdmin && m.UserId != memberUserId);
            if (otherAdmins == 0)
            {
                return false; // Can't remove the last admin
            }
        }

        // Remove the member
        family.Members.Remove(memberToRemove);
        await _dataService.UpdateFamilyAsync(family);
        
        return true;
    }
}