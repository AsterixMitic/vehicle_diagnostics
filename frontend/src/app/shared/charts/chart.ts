import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { EChartsCoreOption } from 'echarts/core';
import { NgxEchartsDirective } from 'ngx-echarts';

/** One chart; the only place that touches ngx-echarts, so tests replace it without a canvas. */
@Component({
  selector: 'vd-chart',
  imports: [NgxEchartsDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div
    echarts
    [options]="options()"
    [autoResize]="true"
    [style.height.px]="height()"
  ></div>`,
  styles: `
    :host {
      display: block;
    }
  `,
})
export class Chart {
  readonly options = input.required<EChartsCoreOption>();
  readonly height = input(240);
}
