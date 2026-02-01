# Plan: MSAL Azure AD Auth with OBO for Azure OpenAI

Implement Azure AD authentication using MSAL in Angular. The .NET API validates JWT, uses OBO flow to get Azure OpenAI token scoped to `https://cognitiveservices.azure.com/.default`, and calls Azure OpenAI REST API. Returns 403 if user lacks access. Unauthenticated users can view telemetry but AI Copilot is disabled.

## Steps

### 1. Add Angular environment configuration
Create `src/environments/environment.ts` with `clientId`, `tenantId`, `redirectUri`, API scope (`api://{api-client-id}/access_as_user`), and `apiUrl`. Update `angular.json` with `fileReplacements`.

### 2. Install and configure MSAL in Angular
Add `@azure/msal-angular`, `@azure/msal-browser`. Configure `MsalModule` in `app.config.ts`. Create `auth.service.ts` with `login()`, `logout()`, `isAuthenticated$`, `getToken()`.

### 3. Create conditional auth interceptor
Create `auth.interceptor.ts` that attaches bearer token only for `/api/v1/ai/*` endpoints when authenticated.

### 4. Update UI for conditional Copilot
Modify `app.component.ts` with login/logout button. Disable Copilot button when unauthenticated. Update `ai-panel.component.ts` to show sign-in prompt for unauthenticated users.

### 5. Configure .NET API authentication
Add `Microsoft.Identity.Web` NuGet. Update `Program.cs` with `AddMicrosoftIdentityWebApiAuthentication()`, `.EnableTokenAcquisitionToCallDownstreamApi(["https://cognitiveservices.azure.com/.default"])`, `AddInMemoryTokenCaches()`. Add `AzureAd` section to `appsettings.json`.

### 6. Create AiController with OBO
Create `Controllers/AiController.cs` with `[Authorize]`. Inject `ITokenAcquisition`, call `GetAccessTokenForUserAsync()` for Azure OpenAI scope. Make HTTP POST to `https://{resource}.openai.azure.com/openai/deployments/{deployment}/chat/completions` with Bearer token. Return 403 on `MsalUiRequiredException`.

### 7. Update AIAssistantService
Replace mock in `ai-assistant.service.ts` with HTTP POST to `/api/v1/ai/chat`. Handle 401 (prompt login) and 403 (show access denied message).

## Configuration Required

```
Azure AD App Registrations:
├── SPA App (Angular)
│   ├── Redirect URI: http://localhost:4200
│   └── API Permission: api://{api-client-id}/access_as_user
│
└── API App (.NET)
    ├── Expose API: access_as_user scope
    └── No Azure OpenAI API permission needed (uses RBAC)

Azure OpenAI Resource:
└── IAM: AD Group → "Cognitive Services OpenAI User" role
```

## File Changes Summary

### Angular Frontend (otel-dashboard-frontend)

| File | Action |
|------|--------|
| `package.json` | Add `@azure/msal-angular`, `@azure/msal-browser` |
| `src/environments/environment.ts` | **Create** - Azure AD config |
| `src/environments/environment.prod.ts` | **Create** - Production config |
| `angular.json` | Add `fileReplacements` for environments |
| `src/app/app.config.ts` | Add MSAL providers |
| `src/app/core/services/auth.service.ts` | **Create** - Auth wrapper service |
| `src/app/core/interceptors/auth.interceptor.ts` | **Create** - Conditional token attachment |
| `src/app/app.component.ts` | Add login/logout button, conditional Copilot |
| `src/app/shared/components/ai-panel/ai-panel.component.ts` | Add sign-in prompt for unauthenticated |
| `src/app/core/services/ai-assistant.service.ts` | Replace mock with real API calls |

### .NET API (otel-dashboard-api)

| File | Action |
|------|--------|
| `OtelDashboardApi.csproj` | Add `Microsoft.Identity.Web` |
| `appsettings.json` | Add `AzureAd`, `AzureOpenAI` sections |
| `Program.cs` | Add authentication middleware with OBO |
| `Controllers/AiController.cs` | **Create** - AI endpoint with OBO flow |
