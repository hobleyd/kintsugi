import 'dart:async';

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../core/di/locator.dart';
import '../../core/network/api_exception.dart';
import '../../core/router/app_router.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/device_report.dart';
import '../../domain/usecases/device_report_usecases.dart';
import 'device_health_row.dart';
import 'device_report_model.dart';

/// "Find a device": opens the report of any device by name or serial number, flagged or not.
///
/// Waits 300 ms after the last key, asks nothing under two characters, and drops an answer that
/// arrives after a newer question was asked, so a slow early reply cannot overwrite a later one.
class DeviceSearch extends StatefulWidget {
  const DeviceSearch({super.key});

  static const delay = Duration(milliseconds: 300);
  static const minLength = 2;
  static const take = 10;

  @override
  State<DeviceSearch> createState() => _DeviceSearchState();
}

class _DeviceSearchState extends State<DeviceSearch> {
  final _controller = TextEditingController();
  Timer? _debounce;
  int _ticket = 0;
  List<DeviceSearchResult>? _results;
  bool _loading = false;
  String? _error;

  @override
  void dispose() {
    _debounce?.cancel();
    _controller.dispose();
    super.dispose();
  }

  void _changed(String value) {
    _debounce?.cancel();
    final text = value.trim();
    if (text.length < DeviceSearch.minLength) {
      // Blank or too short: clear, and make any answer still on its way stale.
      _ticket++;
      setState(() {
        _results = null;
        _error = null;
        _loading = false;
      });
      return;
    }
    _debounce = Timer(DeviceSearch.delay, () => _run(text));
  }

  Future<void> _run(String text) async {
    final ticket = ++_ticket;
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final found = await locator<SearchDevices>()(text, take: DeviceSearch.take);
      if (!mounted || ticket != _ticket) return;
      setState(() {
        _results = found.take(DeviceSearch.take).toList();
        _loading = false;
      });
    } on ApiException catch (error) {
      if (!mounted || ticket != _ticket) return;
      setState(() {
        _error = 'Could not search: ${error.message}';
        _results = null;
        _loading = false;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final results = _results;

    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      SizedBox(
        width: 360,
        child: LabelledField(
          label: 'Find a device',
          hints: const [HintText("Name or serial number. Opens that device's report, flagged or not.")],
          child: KintsugiTextField(controller: _controller, onChanged: _changed),
        ),
      ),
      if (_error != null) Padding(padding: const EdgeInsets.only(top: 6), child: HintText(_error!)),
      if (results != null && results.isEmpty && !_loading)
        const Padding(padding: EdgeInsets.only(top: 6), child: HintText('No device matches')),
      if (results != null && results.isNotEmpty) ...[
        const SizedBox(height: 14),
        Text('SEARCH RESULTS', style: Theme.of(context).textTheme.labelLarge),
        for (final r in results)
          DeviceHealthRowCard(
            hostname: r.hostname,
            detail: r.why.isNotEmpty
                ? r.why
                : '${r.operatingSystem ?? platformLabel(r.platform)}, no unexpected shutdowns or hardware errors in 30 days',
            status: r.status,
            lastEventUtc: r.lastEventUtc,
            onOpenReport: () => context.go(Routes.deviceReport(r.hostId)),
          ),
        const SizedBox(height: 14),
      ],
    ]);
  }
}
