import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatProgressBarModule } from '@angular/material/progress-bar';

import { problemMessage } from '../../core/api/problem';

/** What `DataState` needs of a resource; `httpResource` satisfies it. */
export interface DataSource {
  isLoading(): boolean;
  error(): unknown;
  hasValue(): boolean;
}

/**
 * Loading, empty and error states of one data-bound element (spec: every data view has them),
 * so a failure is confined to the element whose request failed. While a new request replaces
 * shown data, the data stays visible under a thin progress bar instead of blanking.
 */
@Component({
  selector: 'vd-data-state',
  imports: [MatProgressBarModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (source().error(); as error) {
      <p class="state error" role="alert">{{ message(error) }}</p>
    } @else if (!source().hasValue()) {
      <div class="state loading" aria-busy="true">
        <mat-progress-bar mode="indeterminate" aria-label="Учитавање" />
        <span>Учитавање…</span>
      </div>
    } @else if (empty()) {
      <p class="state empty">{{ emptyText() }}</p>
    } @else {
      @if (source().isLoading()) {
        <mat-progress-bar class="refresh" mode="indeterminate" aria-label="Освежавање" />
      }
      <ng-content />
    }
  `,
  styles: `
    :host {
      display: block;
      position: relative;
    }
    .state {
      margin: 0;
      padding: 24px 16px;
      text-align: center;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-medium);
    }
    .loading {
      display: grid;
      gap: 12px;
    }
    .error {
      color: var(--mat-sys-error);
    }
    .refresh {
      position: absolute;
      inset: 0 0 auto;
    }
  `,
})
export class DataState {
  readonly source = input.required<DataSource>();
  readonly empty = input(false);
  readonly emptyText = input('Нема података за изабрани опсег.');

  protected readonly message = problemMessage;
}
