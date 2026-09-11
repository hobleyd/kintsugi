import 'package:flutter/material.dart';

import '../../core/widgets/page_scaffold.dart';
import 'cpe_mapping_queue.dart';

/// The CPE mapping queue, on its own address.
///
/// It began as the second half of the CVE Reporting screen, on the reasoning that an empty
/// findings table is where somebody notices the queue needs draining. That reasoning holds for
/// noticing and not for doing: draining three hundred rows is a session of its own, and it was
/// happening below a findings table that does not change until the next assessment run — so every
/// scroll, every filter and every bookmark carried a page of CVEs that had nothing to do with the
/// work. The two screens keep the connection in words instead: CVE Reporting names this one
/// wherever it reports a gap in its own coverage.
///
/// A thin scaffold around [CpeMappingQueue] rather than a screen with logic of its own — the queue
/// owns its BLoC, and keeping it a widget is what lets `cpe_mapping_queue_test.dart` pump it
/// without a router.
class CveMappingScreen extends StatelessWidget {
  const CveMappingScreen({super.key});

  @override
  Widget build(BuildContext context) => const PageScaffold(
        title: 'CVE Mapping',
        subtitle: 'NVD indexes vulnerabilities by CPE — a vendor and product name that is often '
            'not what the software calls itself. Nothing is assessed for an application until '
            'somebody confirms which one it is.',
        children: [CpeMappingQueue()],
      );
}
