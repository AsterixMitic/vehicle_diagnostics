/**
 * ECharts with only the parts the dashboard uses (design D6). Loaded lazily through
 * `provideEchartsCore`, so none of it is in the initial bundle.
 */
import { BarChart, LineChart } from 'echarts/charts';
import {
  DatasetComponent,
  GridComponent,
  LegendComponent,
  MarkLineComponent,
  TooltipComponent,
} from 'echarts/components';
import * as echarts from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';

echarts.use([
  LineChart,
  BarChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  DatasetComponent,
  MarkLineComponent,
  CanvasRenderer,
]);

export default echarts;
