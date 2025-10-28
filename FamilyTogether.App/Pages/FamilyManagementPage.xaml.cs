using FamilyTogether.App.Services;
using FamilyTogether.App.Models;
using System.Collections.ObjectModel;

namespace FamilyTogether.App.Pages;

public partial class FamilyManagementPage : ContentPage
{
    private readonly ApiService _apiService;
    private Family? _currentFamily;
    private ObservableCollection<FamilyMemberViewModel> _members = new();

    public FamilyManagementPage(ApiService apiService)
    {
        InitializeComponent();
        _apiService = apiService;
        MembersCollectionView.ItemsSource = _members;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadFamilyDataAsync();
    }

    private async Task LoadFamilyDataAsync()
    {
        await SetLoading(true);

        try
        {
            var response = await _apiService.GetFamilyMembersAsync();
            
            if (response.Success && response.Data != null)
            {
                _currentFamily = response.Data.Family;
                _members.Clear();
                
                foreach (var member in response.Data.Members)
                {
                    _members.Add(new FamilyMemberViewModel(member));
                }

                ShowFamilyInfo();
            }
            else
            {
                ShowNoFamily();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Error cargando familia: {ex.Message}", "OK");
            ShowNoFamily();
        }
        finally
        {
            await SetLoading(false);
        }
    }

    private void ShowFamilyInfo()
    {
        if (_currentFamily != null)
        {
            FamilyNameLabel.Text = _currentFamily.Name;
            FamilyCodeLabel.Text = _currentFamily.FamilyGuid;
            
            FamilyInfoFrame.IsVisible = true;
            MembersFrame.IsVisible = true;
            NoFamilyLayout.IsVisible = false;
        }
    }

    private void ShowNoFamily()
    {
        FamilyInfoFrame.IsVisible = false;
        MembersFrame.IsVisible = false;
        NoFamilyLayout.IsVisible = true;
    }

    private async void OnCreateFamilyClicked(object sender, EventArgs e)
    {
        var familyName = await DisplayPromptAsync(
            "Crear Familia", 
            "Ingresa el nombre de tu familia:", 
            "Crear", 
            "Cancelar",
            placeholder: "Ej: Familia García");

        if (!string.IsNullOrWhiteSpace(familyName))
        {
            await SetLoading(true);

            try
            {
                var response = await _apiService.CreateFamilyAsync(familyName.Trim());
                
                if (response.Success)
                {
                    await DisplayAlert("Éxito", "Familia creada exitosamente", "OK");
                    await LoadFamilyDataAsync();
                }
                else
                {
                    await DisplayAlert("Error", response.Message, "OK");
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", $"Error creando familia: {ex.Message}", "OK");
            }
            finally
            {
                await SetLoading(false);
            }
        }
    }

    private async void OnJoinFamilyClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("JoinFamilyPage");
    }

    private async void OnCopyCodeClicked(object sender, EventArgs e)
    {
        if (_currentFamily != null)
        {
            await Clipboard.SetTextAsync(_currentFamily.FamilyGuid);
            await DisplayAlert("Copiado", "Código copiado al portapapeles", "OK");
        }
    }

    private async void OnShareCodeClicked(object sender, EventArgs e)
    {
        if (_currentFamily != null)
        {
            await Share.RequestAsync(new ShareTextRequest
            {
                Text = $"Únete a mi familia en FamilyTogether con el código: {_currentFamily.FamilyGuid}",
                Title = "Código de Familia - FamilyTogether"
            });
        }
    }

    private async void OnApproveMemberClicked(object sender, EventArgs e)
    {
        if (sender is Button button && button.CommandParameter is int userId)
        {
            var member = _members.FirstOrDefault(m => m.UserId == userId);
            if (member == null) return;

            var result = await DisplayAlert(
                "Aprobar Miembro", 
                $"¿Deseas aprobar a {member.Name} como miembro de la familia?", 
                "Aprobar", 
                "Cancelar");

            if (result)
            {
                await SetLoading(true);

                try
                {
                    // Implementar llamada a API para aprobar miembro
                    // var response = await _apiService.ApproveMemberAsync(userId);
                    
                    await DisplayAlert("Éxito", "Miembro aprobado exitosamente", "OK");
                    await LoadFamilyDataAsync();
                }
                catch (Exception ex)
                {
                    await DisplayAlert("Error", $"Error aprobando miembro: {ex.Message}", "OK");
                }
                finally
                {
                    await SetLoading(false);
                }
            }
        }
    }

    private async void OnRemoveMemberClicked(object sender, EventArgs e)
    {
        if (sender is Button button && button.CommandParameter is int userId)
        {
            var member = _members.FirstOrDefault(m => m.UserId == userId);
            if (member == null) return;

            var result = await DisplayAlert(
                "Remover Miembro", 
                $"¿Estás seguro de que deseas remover a {member.Name} de la familia?", 
                "Remover", 
                "Cancelar");

            if (result)
            {
                await SetLoading(true);

                try
                {
                    // Implementar llamada a API para remover miembro
                    // var response = await _apiService.RemoveMemberAsync(userId);
                    
                    await DisplayAlert("Éxito", "Miembro removido exitosamente", "OK");
                    await LoadFamilyDataAsync();
                }
                catch (Exception ex)
                {
                    await DisplayAlert("Error", $"Error removiendo miembro: {ex.Message}", "OK");
                }
                finally
                {
                    await SetLoading(false);
                }
            }
        }
    }

    private async Task SetLoading(bool isLoading)
    {
        LoadingIndicator.IsVisible = isLoading;
        LoadingIndicator.IsRunning = isLoading;
    }
}

public class FamilyMemberViewModel
{
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsPending => Status == "pending";

    public FamilyMemberViewModel(FamilyMember member)
    {
        UserId = member.UserId;
        Name = member.Name;
        Email = member.Email;
        IsAdmin = member.IsAdmin;
        Status = member.Status;
    }
}