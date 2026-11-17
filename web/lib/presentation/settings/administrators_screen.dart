import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import '../session/session_bloc.dart';
import 'admin_email_parsing.dart';
import 'administrators_bloc.dart';

/// Who may administer this site: the list Settings > Authentication also edits as a text box, shown
/// one name per row with who added each and when, and a delete button inside each.
///
/// Each add or delete saves at once — it is one entry changing, not a form being submitted — and
/// the server holds it to the same lockout guard as the Authentication screen: the caller cannot
/// delete themselves while sign-in is required, nor leave a non-empty list without their own email.
class AdministratorsScreen extends StatelessWidget {
  const AdministratorsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => AdministratorsBloc(
          getAdministrators: locator<GetAdministrators>(),
          addAdministrators: locator<AddAdministrators>(),
          removeAdministrator: locator<RemoveAdministrator>(),
        )..add(const AdministratorsRequested()),
        child: const _AdministratorsView(),
      );
}

class _AdministratorsView extends StatefulWidget {
  const _AdministratorsView();

  @override
  State<_AdministratorsView> createState() => _AdministratorsViewState();
}

class _AdministratorsViewState extends State<_AdministratorsView> {
  final _input = TextEditingController();

  /// Which rows are open. Held here rather than in each row so a list that reloads after a change
  /// does not close the ones the administrator was reading.
  final _open = <String>{};

  /// A message about what was typed, kept apart from the server's errors because it is decided
  /// before anything is sent.
  String? _inputError;

  @override
  void dispose() {
    _input.dispose();
    super.dispose();
  }

  void _add() {
    final emails = parseAdminEmails(_input.text);
    if (emails.isEmpty) {
      setState(() => _inputError = 'Enter an email address.');
      return;
    }
    setState(() => _inputError = null);
    context.read<AdministratorsBloc>().add(AdministratorsAddRequested(emails));
  }

  Future<void> _confirmDelete(Administrator admin) async {
    final bloc = context.read<AdministratorsBloc>();
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: dialogContext.palette.backgroundAlt,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(6),
          side: BorderSide(color: dialogContext.palette.border),
        ),
        title: Text('Delete administrator?', style: Theme.of(dialogContext).textTheme.titleLarge),
        content: HintText(
          '${admin.email} will lose access immediately and see "Access denied".',
        ),
        actions: [
          SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(dialogContext).pop(false)),
          PrimaryButton(label: 'Delete', onPressed: () => Navigator.of(dialogContext).pop(true)),
        ],
      ),
    );
    if (confirmed == true && mounted) {
      setState(() => _open.remove(admin.email));
      bloc.add(AdministratorsRemoveRequested(admin.email));
    }
  }

  @override
  Widget build(BuildContext context) {
    final session = context.watch<SessionBloc>().state;
    final ready = session is SessionReady ? session.session : null;
    final me = ready?.email?.toLowerCase();

    return BlocBuilder<AdministratorsBloc, AdministratorsState>(
      builder: (context, state) {
        final admins = state.administrators;

        return PageScaffold(
          title: 'Administrators',
          subtitle: 'People who can sign in and manage Kintsugi. Everyone else sees "Access denied".',
          children: [
            if (state.error != null) AlertBox.error(state.error!),
            if (state.notice != null) AlertBox.success(state.notice!),
            if (ready != null && !ready.authenticationEnabled)
              const AlertBox.info(
                'Sign-in is not required, so this list is not enforced yet. Turn it on under '
                'Settings > Authentication.',
              ),
            if (ready != null && ready.authenticationEnabled && admins != null && admins.isEmpty)
              const AlertBox.info(
                'The list is empty, so every account that can sign in is an administrator. Add at '
                'least one address, starting with your own.',
              ),
            KintsugiPanel(
              padding: const EdgeInsets.all(20),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(
                        child: KintsugiTextField(
                          controller: _input,
                          hintText: 'name@example.com, or paste "Jake Uren <jake.uren@example.com>"',
                          errorText: _inputError,
                          enabled: !state.busy,
                          onChanged: (_) {
                            if (_inputError != null) setState(() => _inputError = null);
                          },
                          onEditingComplete: _add,
                        ),
                      ),
                      const SizedBox(width: 12),
                      PrimaryButton(
                        label: 'Add administrator',
                        busy: state.busy,
                        onPressed: state.loading || admins == null ? null : _add,
                      ),
                    ],
                  ),
                  const SizedBox(height: 8),
                  const HintText(
                    'Several can be added at once, one per line or separated by commas. A display '
                    'name is removed automatically. The address must be one the sign-in provider has '
                    'verified.',
                  ),
                ],
              ),
            ),
            const SizedBox(height: 20),
            KintsugiPanel(
              padding: const EdgeInsets.all(20),
              clip: false,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  SectionHeader(
                    title: 'Accepted administrators',
                    actions: [if (admins != null) CountBadge(admins.length)],
                  ),
                  if (state.loading && admins == null)
                    const HintText('Loading…')
                  else if (admins == null)
                    const HintText('The list could not be loaded.')
                  else if (admins.isEmpty)
                    const HintText('No administrators set.')
                  else
                    for (final admin in admins) ...[
                      _AdministratorRow(
                        key: ValueKey('admin-row-${admin.email}'),
                        admin: admin,
                        isMe: admin.email.toLowerCase() == me,
                        isLast: admins.length == 1,
                        open: _open.contains(admin.email),
                        busy: state.busy,
                        onToggle: () => setState(
                          () => _open.contains(admin.email) ? _open.remove(admin.email) : _open.add(admin.email),
                        ),
                        onDelete: () => _confirmDelete(admin),
                      ),
                      const SizedBox(height: 8),
                    ],
                  const SizedBox(height: 4),
                  const HintText(
                    'Click a name to see its details. Changes save immediately. Where "Added by" shows '
                    'a dash, the address was on the list before this was recorded, or was typed into '
                    'Settings > Authentication.',
                  ),
                ],
              ),
            ),
          ],
        );
      },
    );
  }
}

/// One name. Closed it is only the name, and it lights up under the pointer the way the sidebar's
/// links do; open, the details and the delete button sit beneath it.
class _AdministratorRow extends StatefulWidget {
  const _AdministratorRow({
    super.key,
    required this.admin,
    required this.isMe,
    required this.isLast,
    required this.open,
    required this.busy,
    required this.onToggle,
    required this.onDelete,
  });

  final Administrator admin;
  final bool isMe;

  /// The only entry left. The server refuses to remove it — an empty list makes every account that
  /// can sign in an administrator — so the row says why instead of offering a Delete that fails.
  final bool isLast;
  final bool open;
  final bool busy;
  final VoidCallback onToggle;
  final VoidCallback onDelete;

  @override
  State<_AdministratorRow> createState() => _AdministratorRowState();
}

class _AdministratorRowState extends State<_AdministratorRow> {
  bool _hovering = false;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final lit = _hovering || widget.open;
    final admin = widget.admin;

    return MouseRegion(
      onEnter: (_) => setState(() => _hovering = true),
      onExit: (_) => setState(() => _hovering = false),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 120),
        decoration: BoxDecoration(
          border: Border.all(color: lit ? palette.neon : palette.border),
          borderRadius: BorderRadius.circular(3),
          boxShadow: lit && palette.glowsEnabled
              ? [BoxShadow(color: palette.neon.withValues(alpha: 0.35), blurRadius: 12)]
              : const [],
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Semantics(
              button: true,
              expanded: widget.open,
              label: admin.email,
              child: InkWell(
                onTap: widget.onToggle,
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
                  child: Row(
                    children: [
                      Icon(
                        widget.open ? Icons.keyboard_arrow_down : Icons.keyboard_arrow_right,
                        size: 18,
                        color: lit ? palette.neon : palette.muted,
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          admin.email,
                          style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                                color: lit ? palette.neon : palette.text,
                              ),
                        ),
                      ),
                      if (widget.isMe) const StatusChip.neutral('You'),
                    ],
                  ),
                ),
              ),
            ),
            if (widget.open)
              Container(
                decoration: BoxDecoration(border: Border(top: BorderSide(color: palette.border))),
                padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 14),
                child: Wrap(
                  spacing: 32,
                  runSpacing: 12,
                  crossAxisAlignment: WrapCrossAlignment.center,
                  children: [
                    const _Detail('Role', StatusChip('Admin', statusKey: 'online')),
                    _Detail('Added by', admin.addedBy == null ? const NoValue() : Text(admin.addedBy!)),
                    _Detail('Added on', LocalTimestamp(admin.addedUtc)),
                    widget.isMe
                        ? const HintText("You can't delete yourself: you would lock yourself out.")
                        : widget.isLast
                        ? const HintText(
                            "The last administrator can't be deleted: an empty list makes every account that can "
                            'sign in an administrator. Add another first, or clear the list in Settings > Authentication.',
                          )
                        : _DeleteButton(
                            key: ValueKey('admin-delete-${admin.email}'),
                            onPressed: widget.busy ? null : widget.onDelete,
                          ),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _Detail extends StatelessWidget {
  const _Detail(this.label, this.value);

  final String label;
  final Widget value;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(label.toUpperCase(), style: Theme.of(context).textTheme.labelLarge),
          const SizedBox(height: 4),
          value,
        ],
      );
}

/// A destructive action in the app's red, outlined like [SecondaryButton] so it reads as part of
/// the same family rather than a second kind of button.
class _DeleteButton extends StatelessWidget {
  const _DeleteButton({super.key, required this.onPressed});

  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final color = onPressed == null ? palette.muted : palette.red;
    return OutlinedButton.icon(
      onPressed: onPressed,
      icon: Icon(Icons.delete_outline, size: 16, color: color),
      label: Text('DELETE', style: AppTheme.display(color: color, size: 10.9, letterSpacing: 0.87)),
      style: OutlinedButton.styleFrom(
        foregroundColor: palette.red,
        side: BorderSide(color: color),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(3)),
      ),
    );
  }
}
