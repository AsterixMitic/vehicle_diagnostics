import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatTableModule } from '@angular/material/table';
import { Router, RouterLink } from '@angular/router';

import { ApiUrls } from '../../core/api/api-urls';
import { Device } from '../../core/api/models';
import { formatDateTime } from '../../core/time/format';
import { rangeFromQuery, rangeToQuery, RangeSelection, resolveRange } from '../../core/time/range';
import { DataState } from '../../shared/data-state/data-state';
import { ModeBadge } from '../../shared/mode-badge';
import { RangePicker } from '../../shared/range-picker/range-picker';

/** Devices with telemetry in a range, linking to their live and history views. */
@Component({
  selector: 'vd-devices',
  imports: [DataState, RangePicker, ModeBadge, MatTableModule, MatButtonModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './devices.html',
  styleUrl: './devices.scss',
})
export class Devices {
  // Query parameters, bound by the router (design D4).
  readonly range = input<string | null>(null);
  readonly from = input<string | null>(null);
  readonly to = input<string | null>(null);

  private readonly router = inject(Router);
  private readonly refreshes = signal(0);

  protected readonly selection = computed(() =>
    rangeFromQuery({ range: this.range(), from: this.from(), to: this.to() }),
  );

  protected readonly devices = httpResource<Device[]>(() => {
    this.refreshes();
    return ApiUrls.devices(resolveRange(this.selection(), new Date()));
  });

  protected readonly columns = ['device', 'vehicle', 'lastSeen', 'mode', 'links'];
  protected readonly formatDateTime = formatDateTime;

  protected select(selection: RangeSelection): void {
    void this.router.navigate([], {
      queryParams: rangeToQuery(selection),
      queryParamsHandling: 'merge',
    });
  }

  /** A preset range moves with time, so a refresh re-resolves it. */
  protected refresh(): void {
    this.refreshes.update((n) => n + 1);
  }
}
