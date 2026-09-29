import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'vd-devices',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<h1>Уређаји</h1>`,
})
export class Devices {}
