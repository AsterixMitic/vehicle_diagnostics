import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';

import { EvaluationMode } from '../core/api/models';
import { ColorScheme, modeColor } from './charts/chart-theme';

/** A processing mode as text keyed by its chart color; the text carries identity, not the color. */
@Component({
  selector: 'vd-mode-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="key" [style.background]="color()"></span>{{ label() }}`,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-variant-numeric: tabular-nums;
    }
    .key {
      width: 14px;
      height: 3px;
      border-radius: 2px;
    }
  `,
})
export class ModeBadge {
  readonly mode = input.required<EvaluationMode>();

  private readonly scheme = inject(ColorScheme).scheme;
  protected readonly color = computed(() => modeColor(this.mode(), this.scheme()));
  protected readonly label = computed(() => (this.mode() === 'unknown' ? 'непознат' : this.mode()));
}
