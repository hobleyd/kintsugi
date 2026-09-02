import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/injection.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import '../session/session_bloc.dart';
import 'authentication_bloc.dart';
import 'settings_state.dart';

/// The identity provider users sign in through. What `Pages/Settings/Authentication.cshtml` was.
///
/// This is the screen a fresh deploy is pinned to: with no provider saved, the router sends every
/// other path here (see `createRouter`), which is the client half of what `Program.cs`'s
/// fresh-deploy redirect used to do.
class AuthenticationSettingsScreen extends StatelessWidget {
  const AuthenticationSettingsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => AuthenticationSettingsBloc(
          getSettings: locator<GetAuthenticationSettings>(),
          updateSettings: locator<UpdateAuthenticationSettings>(),
        )..add(const AuthenticationSettingsRequested()),
        child: const _AuthenticationForm(),
      );
}

class _AuthenticationForm extends StatefulWidget {
  const _AuthenticationForm();

  @override
  State<_AuthenticationForm> createState() => _AuthenticationFormState();
}

class _AuthenticationFormState extends State<_AuthenticationForm> {
  final _clientId = TextEditingController();
  final _clientSecret = TextEditingController();
  final _authority = TextEditingController();
  final _tenantId = TextEditingController();
  final _hostedDomain = TextEditingController();

  AuthProvider _provider = AuthProvider.googleWorkspace;
  bool _isEnabled = false;

  @override
  void dispose() {
    _clientId.dispose();
    _clientSecret.dispose();
    _authority.dispose();
    _tenantId.dispose();
    _hostedDomain.dispose();
    super.dispose();
  }

  void _hydrate(AuthenticationSettings settings) {
    _clientId.text = settings.clientId ?? '';
    _authority.text = settings.authority ?? '';
    _tenantId.text = settings.tenantId ?? '';
    _hostedDomain.text = settings.hostedDomain ?? '';
    _clientSecret.clear();
    setState(() {
      _provider = settings.provider;
      _isEnabled = settings.isEnabled;
    });
  }

  void _edited() => context.read<AuthenticationSettingsBloc>().add(const AuthenticationSettingsEdited());

  void _save() =>
      context.read<AuthenticationSettingsBloc>().add(AuthenticationSettingsSaveRequested(
            provider: _provider,
            clientId: _clientId.text.trim(),
            clientSecret: _clientSecret.text.isEmpty ? null : _clientSecret.text,
            authority: _authority.text.trim(),
            tenantId: _tenantId.text.trim(),
            hostedDomain: _hostedDomain.text.trim(),
            isEnabled: _isEnabled,
          ));

  @override
  Widget build(BuildContext context) =>
      BlocConsumer<AuthenticationSettingsBloc, SettingsState<AuthenticationSettings>>(
        listenWhen: (previous, current) =>
            (previous.value != current.value && current.value != null) ||
            (!previous.saved && current.saved),
        listener: (context, state) {
          if (state.value != null) _hydrate(state.value!);

          // A save here changes the answers GET /api/session gives — whether a provider is saved
          // at all, and whether sign-in is required — and those are what the router gates on. So
          // the session is re-read rather than left stale: it is what releases a fresh deploy from
          // this screen, and equally what sends an administrator who has just switched sign-in on
          // to the sign-in screen, which is the documented consequence of switching it on.
          if (state.saved) context.read<SessionBloc>().add(const SessionRequested());
        },
        builder: (context, state) {
          final needsTenant = _provider == AuthProvider.microsoftEntra;
          final needsAuthority =
              _provider == AuthProvider.genericOidc || _provider == AuthProvider.clerk;
          final isGoogle = _provider == AuthProvider.googleWorkspace;

          return PageScaffold(
            title: 'Authentication',
            subtitle: 'Require sign-in through Google Workspace, Microsoft Entra, or another '
                'OAuth2/OIDC provider (Auth0, Okta, Clerk, etc.) before anyone can use this site.',
            children: [
              if (state.error != null) AlertBox.error(state.error!),
              if (state.saved) const AlertBox.success('Settings saved.'),
              SettingsColumns(
                form: SettingsFormPanel(
                  maxWidth: double.infinity,
                  children: [
                    LabelledField(
                      label: 'Provider',
                      child: KintsugiDropdown<AuthProvider>(
                        value: _provider,
                        items: AuthProvider.values,
                        labelOf: (value) => value.label,
                        onChanged: (value) {
                          setState(() => _provider = value);
                          _edited();
                        },
                      ),
                    ),
                    LabelledField(
                      label: 'Client ID',
                      child: KintsugiTextField(
                        controller: _clientId,
                        errorText: state.errorFor('ClientId'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    LabelledField(
                      label: 'Client Secret',
                      hints: [
                        if (state.value?.hasClientSecret == true)
                          const HintText(
                            'A client secret is already configured. Leave blank to keep it.',
                          ),
                      ],
                      child: KintsugiTextField(
                        controller: _clientSecret,
                        obscureText: true,
                        hintText: state.value?.hasClientSecret == true
                            ? '•••••••••••• (leave blank to keep current secret)'
                            : '',
                        errorText: state.errorFor('ClientSecret'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    if (needsTenant)
                      LabelledField(
                        label: 'Tenant ID',
                        hints: const [HintText('Your Microsoft Entra directory (tenant) ID.')],
                        child: KintsugiTextField(
                          controller: _tenantId,
                          hintText: 'e.g. contoso.onmicrosoft.com or a GUID',
                          errorText: state.errorFor('TenantId'),
                          onChanged: (_) => _edited(),
                        ),
                      ),
                    if (needsAuthority)
                      LabelledField(
                        label: 'Authority (issuer URL)',
                        hints: const [
                          HintText(
                            'The base issuer URL your provider publishes discovery metadata under '
                            '(/.well-known/openid-configuration).',
                          ),
                        ],
                        child: KintsugiTextField(
                          controller: _authority,
                          hintText: 'https://your-tenant.us.auth0.com',
                          errorText: state.errorFor('Authority'),
                          onChanged: (_) => _edited(),
                        ),
                      ),
                    if (isGoogle)
                      LabelledField(
                        label: 'Restrict to Workspace domain (optional)',
                        hints: const [
                          HintText(
                            'Leave blank to allow sign-in from any Google account. Set this to only '
                            'allow accounts in your Google Workspace domain.',
                          ),
                        ],
                        child: KintsugiTextField(
                          controller: _hostedDomain,
                          hintText: 'e.g. example.com',
                          errorText: state.errorFor('HostedDomain'),
                          onChanged: (_) => _edited(),
                        ),
                      ),
                    KintsugiCheckbox(
                      label: 'Require sign-in to access this site',
                      value: _isEnabled,
                      onChanged: (value) {
                        setState(() => _isEnabled = value);
                        _edited();
                      },
                    ),
                    const HintText(
                      'Make sure the provider above is fully configured and working before enabling '
                      "this — there is no other way back into the site once it's on except signing in.",
                    ),
                    Align(
                      alignment: Alignment.centerLeft,
                      child: PrimaryButton(
                        label: 'Save Settings',
                        busy: state.saving,
                        onPressed: state.loading ? null : _save,
                      ),
                    ),
                  ],
                ),
                aside: _SetupInstructions(provider: _provider),
              ),
            ],
          );
        },
      );
}

/// Setup instructions for whichever provider is selected.
///
/// These restate decisions that live in code, so keep them in step with it: which fields a
/// provider requires is `UpdateAuthenticationSettingsCommandValidator`, what the issuer URL ends
/// up being is `AuthenticationSettings.ResolveAuthority`, and the fixed scopes, the code flow and
/// the Google hosted-domain enforcement are `DynamicOpenIdConnectOptionsConfigurator`.
///
/// Keyed on the exact provider rather than on the same condition the fields above use, because
/// that condition groups Clerk with the generic OIDC case — correct for the fields, wrong for the
/// instructions.
class _SetupInstructions extends StatelessWidget {
  const _SetupInstructions({required this.provider});

  final AuthProvider provider;

  @override
  Widget build(BuildContext context) {
    final session = context.watch<SessionBloc>().state;
    final urls = session is SessionReady ? session.session : null;

    return SettingsAside(
      title: 'Setting this up',
      children: [
        const HintText(
          'Whichever provider you use, register this site as a confidential web application — the '
          'sign-in uses the authorization code flow and always sends a client secret, so an app '
          'registered as a single-page or native/mobile client will be rejected by the provider. It '
          'requests the openid, profile and email scopes.',
        ),
        const SizedBox(height: 12),
        const HintText('Register both of these URLs with the provider:'),
        const SizedBox(height: 8),
        _UrlPair(label: 'Redirect URI', value: urls?.callbackUrl),
        _UrlPair(label: 'Post-sign-out redirect URI', value: urls?.signOutCallbackUrl),
        const SizedBox(height: 8),
        const HintText(
          'The second is not optional: logging out signs out of the provider too, so without it the '
          'provider rejects the sign-out. Most providers take it as a second entry in the same '
          'redirect-URI list.',
        ),
        const SizedBox(height: 8),
        const HintText(
          'Both are built from the address this screen was reached on. If they do not match what is '
          "in your browser's address bar, a proxy in front of this site is not passing the original "
          'host through (X-Forwarded-Host) — fix that first, because sign-in will fail at the '
          'provider whatever you register here.',
        ),
        const SizedBox(height: 20),
        ..._providerSteps(context),
      ],
    );
  }

  List<Widget> _providerSteps(BuildContext context) => switch (provider) {
        AuthProvider.googleWorkspace => [
            const SubHeadingTight('Google Workspace'),
            const NumberedSteps([
              HintText(
                'In the Google Cloud console, open APIs & Services → Credentials and choose Create '
                'credentials → OAuth client ID.',
              ),
              HintText('Set the application type to Web application.'),
              HintText('Add the redirect URI above under Authorised redirect URIs.'),
              HintText('Copy the generated client ID and client secret into the form.'),
              HintText('Optionally set Restrict to Workspace domain to your Workspace domain.'),
            ]),
            const SizedBox(height: 8),
            const HintText(
              "There is no authority to enter: Google's issuer is always "
              'https://accounts.google.com.',
            ),
            const SizedBox(height: 8),
            const HintText(
              'The domain restriction is enforced, not just a hint to the account chooser — a '
              'sign-in whose account is outside that domain is rejected after the token is '
              'validated.',
            ),
          ],
        AuthProvider.microsoftEntra => [
            const SubHeadingTight('Microsoft Entra'),
            const NumberedSteps([
              HintText('In the Entra admin center, open App registrations → New registration.'),
              HintText(
                'Under Redirect URI, choose the Web platform and enter the redirect URI above.',
              ),
              HintText(
                'Under Certificates & secrets, add a new client secret and copy its Value (not its '
                'ID) into the form.',
              ),
              HintText(
                "Copy the Application (client) ID from the app's Overview page into Client ID.",
              ),
              HintText('Copy the Directory (tenant) ID from the same page into Tenant ID.'),
            ]),
            const SizedBox(height: 8),
            const HintText(
              'The tenant ID is a GUID or a domain such as contoso.onmicrosoft.com — not a URL. The '
              'issuer is built from it as https://login.microsoftonline.com/{tenant}/v2.0.',
            ),
          ],
        AuthProvider.genericOidc => [
            const SubHeadingTight('Generic OAuth2 / OIDC'),
            const NumberedSteps([
              HintText(
                "Create an application of the provider's confidential web type — a Regular Web "
                'Application in Auth0, a Web Application in Okta.',
              ),
              HintText('Add the redirect URI above to its allowed callback / redirect URIs.'),
              HintText(
                'Allow the authorization code grant, and the openid, profile and email scopes.',
              ),
              HintText('Copy its client ID and client secret into the form.'),
              HintText("Enter the provider's base issuer URL as the Authority."),
            ]),
            const SizedBox(height: 8),
            const HintText(
              'The authority must be an absolute URL that serves discovery metadata at '
              '/.well-known/openid-configuration — for example https://your-tenant.us.auth0.com or '
              'https://your-org.okta.com/oauth2/default. Open that URL in a browser to confirm it '
              'returns JSON before saving.',
            ),
          ],
        AuthProvider.clerk => [
            const SubHeadingTight('Clerk'),
            const NumberedSteps([
              HintText('In the Clerk dashboard, create an OAuth application for this site.'),
              HintText('Add the redirect URI above as its redirect URI.'),
              HintText('Grant it the openid, profile and email scopes.'),
              HintText('Copy its client ID and client secret into the form.'),
              HintText(
                "Enter the instance's issuer URL — the Frontend API URL shown in the dashboard, or "
                'your custom Clerk domain if you have set one — as the Authority.',
              ),
            ]),
            const SizedBox(height: 8),
            const HintText(
              'As with any other OIDC provider, that URL must serve '
              '/.well-known/openid-configuration; check it in a browser before saving.',
            ),
          ],
      };
}

class _UrlPair extends StatelessWidget {
  const _UrlPair({required this.label, required this.value});

  final String label;
  final String? value;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 10),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(label.toUpperCase(), style: Theme.of(context).textTheme.labelLarge),
            const SizedBox(height: 3),
            value == null ? const NoValue() : CodeText(value!),
          ],
        ),
      );
}
