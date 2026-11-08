using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace SmartClassAC.Services;

internal static class TeacherAccountValidation
{
    internal static OperationResult ValidateCreate(CreateTeacherRequest request) =>
        Validate(request.DisplayName, request.Username, request.EmployeeNumber, request.Email,
            request.Phone, request.DevicePin, request.Password, passwordRequired: true);

    internal static OperationResult ValidateUpdate(UpdateAdminTeacherRequest request)
    {
        if (request.AccountId <= 0)
            return OperationResult.Failure("Select a teacher account to update.");

        return Validate(request.DisplayName, request.Username, request.EmployeeNumber, request.Email,
            request.Phone, request.DevicePin, request.NewPassword, passwordRequired: false);
    }

    private static OperationResult Validate(string displayName, string username, string employeeNumber,
        string? email, string phone, string? devicePin, string? password, bool passwordRequired)
    {
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(username))
            return OperationResult.Failure("Enter the teacher's name and username.");
        if (string.IsNullOrWhiteSpace(employeeNumber))
            return OperationResult.Failure("Enter the teacher's employee number.");
        if (!string.IsNullOrWhiteSpace(email) && !new EmailAddressAttribute().IsValid(email.Trim()))
            return OperationResult.Failure("Enter a valid school email address, or leave it blank.");
        if (!MobilePhones.IsValid(phone))
            return OperationResult.Failure(MobilePhones.RequirementMessage);
        if ((passwordRequired || !string.IsNullOrWhiteSpace(password)) &&
            (password is null || !PasswordPolicy.MeetsRequirements(password)))
            return OperationResult.Failure(PasswordPolicy.RequirementMessage);
        if (!string.IsNullOrWhiteSpace(devicePin) && !DevicePinCodes.IsValid(DevicePinCodes.Normalize(devicePin)))
            return OperationResult.Failure(DevicePinCodes.RequirementMessage);
        return OperationResult.Success();
    }
}

internal static class MobileVerificationCodes
{
    internal const string RequirementMessage = "Enter the 6-digit verification code sent by SMS.";
    internal static bool IsValid([NotNullWhen(true)] string? code) => code is { Length: 6 } && code.All(char.IsAsciiDigit);
}
