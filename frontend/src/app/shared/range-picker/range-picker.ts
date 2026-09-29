import { ChangeDetectionStrategy, Component, input, linkedSignal, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

import { RANGE_PRESETS, RangePreset, RangeSelection } from '../../core/time/range';

const CUSTOM = 'custom';

/**
 * Preset ranges plus explicit bounds. Explicit bounds are entered in local time and emitted as
 * instants; they are not validated here, so the backend's explanation of a refused range is
 * what the user sees.
 */
@Component({
  selector: 'vd-range-picker',
  imports: [
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatInputModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-button-toggle-group
      [value]="choice()"
      (change)="choose($event.value)"
      aria-label="Временски опсег"
      hideSingleSelectionIndicator
    >
      @for (preset of presets; track preset.value) {
        <mat-button-toggle [value]="preset.value">{{ preset.label }}</mat-button-toggle>
      }
      <mat-button-toggle value="custom">Прилагођено</mat-button-toggle>
    </mat-button-toggle-group>

    @if (choice() === 'custom') {
      <form class="custom" (ngSubmit)="apply()">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Од</mat-label>
          <input matInput type="datetime-local" name="from" [(ngModel)]="fromText" required />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>До</mat-label>
          <input matInput type="datetime-local" name="to" [(ngModel)]="toText" required />
        </mat-form-field>
        <button mat-flat-button type="submit" [disabled]="!fromText() || !toText()">Примени</button>
      </form>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 12px;
    }
    .custom {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 12px;
    }
  `,
})
export class RangePicker {
  readonly selection = input.required<RangeSelection>();
  readonly selectionChange = output<RangeSelection>();

  protected readonly presets = RANGE_PRESETS;

  /** The toggle's state follows the selection, but "custom" can be chosen before applying. */
  protected readonly choice = linkedSignal<RangePreset | typeof CUSTOM>(() => {
    const selection = this.selection();
    return selection.kind === 'preset' ? selection.preset : CUSTOM;
  });

  protected readonly fromText = linkedSignal(() => this.localText('from'));
  protected readonly toText = linkedSignal(() => this.localText('to'));

  protected choose(value: RangePreset | typeof CUSTOM): void {
    this.choice.set(value);
    if (value !== CUSTOM) {
      this.selectionChange.emit({ kind: 'preset', preset: value });
    }
  }

  protected apply(): void {
    const from = new Date(this.fromText());
    const to = new Date(this.toText());
    if (!Number.isNaN(from.getTime()) && !Number.isNaN(to.getTime())) {
      this.selectionChange.emit({ kind: 'explicit', from, to });
    }
  }

  private localText(bound: 'from' | 'to'): string {
    const selection = this.selection();
    return selection.kind === 'explicit' ? toLocalInput(selection[bound]) : '';
  }
}

/** `datetime-local` wants local wall time without an offset. */
function toLocalInput(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}
