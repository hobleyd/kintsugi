import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import '../../core/router/app_router.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/theme/theme_cubit.dart';
import '../session/session_bloc.dart';

/// The sidebar and the page beside it — what `_Layout.cshtml` was.
///
/// A [ShellRoute] rather than a widget each screen wraps itself in, so navigating between screens
/// rebuilds only the page: the sidebar keeps its scroll position and does not flicker, which is
/// most of what "no full page refresh" means in practice.
class AppShell extends StatelessWidget {
  const AppShell({super.key, required this.location, required this.child});

  final String location;
  final Widget child;

  @override
  Widget build(BuildContext context) => Scaffold(
        body: DecoratedBox(
          decoration: BoxDecoration(gradient: _backgroundWash(context)),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _Sidebar(location: location),
              Expanded(child: child),
            ],
          ),
        ),
      );

  /// The two radial washes the body carried — accent from the top, pink from the bottom right.
  /// The 42px grid the stylesheet drew with repeating linear gradients is left out: at Flutter's
  /// rasterisation it reads as moiré rather than as a grid.
  static Gradient _backgroundWash(BuildContext context) {
    final palette = context.palette;
    return LinearGradient(
      begin: Alignment.topCenter,
      end: Alignment.bottomRight,
      colors: [
        palette.neon.withValues(alpha: palette.glowsEnabled ? 0.10 : 0.04),
        palette.background,
        palette.pink.withValues(alpha: palette.glowsEnabled ? 0.06 : 0.02),
      ],
      stops: const [0, 0.55, 1],
    );
  }
}

class _Sidebar extends StatelessWidget {
  const _Sidebar({required this.location});

  final String location;

  /// 240px, as it was — sized so "Authentication" fits on one line in tracked-out Orbitron. It
  /// overflowed at the 210px this started as.
  static const _width = 240.0;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final inSync = location == Routes.clients || location == Routes.upgradeScripts;
    final inSettings = location.startsWith('/settings');

    return Container(
      width: _width,
      decoration: BoxDecoration(
        color: palette.panel,
        border: Border(right: BorderSide(color: palette.border)),
      ),
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(18, 0, 18, 32),
        child: ConstrainedBox(
          // Fills the viewport when the nav is shorter than it, so the footer sits at the bottom;
          // scrolls when it is not, which is what stops a short window clipping the log-out
          // button off with no way back to it.
          constraints: BoxConstraints(minHeight: MediaQuery.sizeOf(context).height - 32),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const _Brand(),
              _NavLink(label: 'Hosts', path: Routes.hosts, selected: location == Routes.hosts),
              const SizedBox(height: 8),
              _NavLink(
                label: 'Applications',
                path: Routes.applications,
                selected: location == Routes.applications,
              ),
              const SizedBox(height: 8),
              _NavLink(label: 'Sync', path: Routes.clients, selected: inSync),
              // Alphabetical by label. Keep it that way when adding one — the list is a lookup,
              // not a workflow, so there is no order to it a reader could otherwise predict.
              _SubNav(
                children: [
                  _SubNavLink(label: 'Clients', path: Routes.clients, selected: location == Routes.clients),
                  _SubNavLink(
                    label: 'Upgrade Scripts',
                    path: Routes.upgradeScripts,
                    selected: location == Routes.upgradeScripts,
                  ),
                ],
              ),
              const SizedBox(height: 8),
              _NavLink(label: 'Settings', path: Routes.settingsAiAgent, selected: inSettings),
              _SubNav(
                children: [
                  _SubNavLink(
                    label: 'AI Agent',
                    path: Routes.settingsAiAgent,
                    selected: location == Routes.settingsAiAgent,
                  ),
                  _SubNavLink(
                    label: 'Authentication',
                    path: Routes.settingsAuthentication,
                    selected: location == Routes.settingsAuthentication,
                  ),
                  _SubNavLink(
                    label: 'GitHub',
                    path: Routes.settingsGitHub,
                    selected: location == Routes.settingsGitHub,
                  ),
                  _SubNavLink(
                    label: 'Patching Policy',
                    path: Routes.settingsPatchingPolicy,
                    selected: location == Routes.settingsPatchingPolicy,
                  ),
                ],
              ),
              const Expanded(child: SizedBox(height: 24)),
              const _SidebarFooter(),
            ],
          ),
        ),
      ),
    );
  }
}

class _Brand extends StatelessWidget {
  const _Brand();

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(top: 24, bottom: 24),
        child: Column(
          children: [
            ClipRRect(
              borderRadius: BorderRadius.circular(10),
              child: Image.asset('assets/img/logo-nav.png'),
            ),
            const SizedBox(height: 10),
            Text(
              'KINTSUGI',
              style: AppTheme.display(
                color: context.palette.neon,
                size: 20,
                weight: FontWeight.w900,
                letterSpacing: 1.6,
              ),
            ),
          ],
        ),
      );
}

class _NavLink extends StatelessWidget {
  const _NavLink({required this.label, required this.path, required this.selected});

  final String label;
  final String path;
  final bool selected;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return _HoverTarget(
      builder: (hovering) {
        final active = selected || hovering;
        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 11),
          decoration: BoxDecoration(
            border: Border.all(color: active ? palette.neon : palette.border),
            borderRadius: BorderRadius.circular(3),
            boxShadow: active && palette.glowsEnabled
                ? [BoxShadow(color: palette.neon.withValues(alpha: 0.35), blurRadius: 12)]
                : const [],
          ),
          child: Text(
            label.toUpperCase(),
            style: AppTheme.display(
              color: active ? palette.neon : palette.neonDim,
              size: 11.5,
              letterSpacing: 1.38,
            ),
          ),
        );
      },
      onTap: () => context.go(path),
      semanticLabel: label,
      selected: selected,
    );
  }
}

class _SubNav extends StatelessWidget {
  const _SubNav({required this.children});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Container(
        margin: const EdgeInsets.only(left: 8, top: 8),
        padding: const EdgeInsets.only(left: 10),
        decoration: BoxDecoration(
          border: Border(left: BorderSide(color: context.palette.border)),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            for (var i = 0; i < children.length; i++) ...[
              if (i > 0) const SizedBox(height: 5),
              children[i],
            ],
          ],
        ),
      );
}

class _SubNavLink extends StatelessWidget {
  const _SubNavLink({required this.label, required this.path, required this.selected});

  final String label;
  final String path;
  final bool selected;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return _HoverTarget(
      builder: (hovering) {
        final active = selected || hovering;
        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 8),
          decoration: BoxDecoration(
            border: Border.all(color: active ? palette.border : Colors.transparent),
            borderRadius: BorderRadius.circular(3),
          ),
          child: Text(
            label.toUpperCase(),
            style: AppTheme.display(
              color: active ? palette.neon : palette.muted,
              size: 9.9,
              weight: FontWeight.w600,
              letterSpacing: 0.99,
            ),
          ),
        );
      },
      onTap: () => context.go(path),
      semanticLabel: label,
      selected: selected,
    );
  }
}

/// A tappable nav entry that restyles on hover, and carries the selected state to assistive
/// technology the way `aria-current="page"` did.
class _HoverTarget extends StatefulWidget {
  const _HoverTarget({
    required this.builder,
    required this.onTap,
    required this.semanticLabel,
    required this.selected,
  });

  final Widget Function(bool hovering) builder;
  final VoidCallback onTap;
  final String semanticLabel;
  final bool selected;

  @override
  State<_HoverTarget> createState() => _HoverTargetState();
}

class _HoverTargetState extends State<_HoverTarget> {
  bool _hovering = false;

  @override
  Widget build(BuildContext context) => Semantics(
        label: widget.semanticLabel,
        button: true,
        selected: widget.selected,
        child: MouseRegion(
          cursor: SystemMouseCursors.click,
          onEnter: (_) => setState(() => _hovering = true),
          onExit: (_) => setState(() => _hovering = false),
          child: GestureDetector(onTap: widget.onTap, child: widget.builder(_hovering)),
        ),
      );
}

/// The theme toggle and, when there is one, the signed-in account.
class _SidebarFooter extends StatelessWidget {
  const _SidebarFooter();

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final session = context.watch<SessionBloc>().state;
    final user = session is SessionReady && session.session.signedIn ? session.session.userName : null;

    return Container(
      padding: const EdgeInsets.only(top: 16),
      decoration: BoxDecoration(border: Border(top: BorderSide(color: palette.border))),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _FooterButton(
            icon: context.watch<ThemeCubit>().state == ThemeMode.light
                ? Icons.dark_mode_outlined
                : Icons.light_mode_outlined,
            label: context.watch<ThemeCubit>().state == ThemeMode.light ? 'Dark mode' : 'Light mode',
            onTap: () => context.read<ThemeCubit>().toggle(),
          ),
          if (user != null) ...[
            const SizedBox(height: 10),
            Text(
              user,
              overflow: TextOverflow.ellipsis,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(color: palette.muted),
            ),
            const SizedBox(height: 8),
            _FooterButton(
              icon: Icons.logout,
              label: 'Log out',
              onTap: () => context.read<SessionBloc>().add(const SignOutRequested()),
            ),
          ],
        ],
      ),
    );
  }
}

class _FooterButton extends StatelessWidget {
  const _FooterButton({required this.icon, required this.label, required this.onTap});

  final IconData icon;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return _HoverTarget(
      selected: false,
      semanticLabel: label,
      onTap: onTap,
      builder: (hovering) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 13, vertical: 9),
        decoration: BoxDecoration(
          border: Border.all(color: hovering ? palette.neon : palette.border),
          borderRadius: BorderRadius.circular(3),
        ),
        child: Row(
          children: [
            Icon(icon, size: 14, color: hovering ? palette.neon : palette.muted),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                label.toUpperCase(),
                style: AppTheme.display(
                  color: hovering ? palette.neon : palette.muted,
                  size: 9.9,
                  weight: FontWeight.w600,
                  letterSpacing: 0.99,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
