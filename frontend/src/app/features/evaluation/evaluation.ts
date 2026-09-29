import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'vd-evaluation',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<h1>Евалуација</h1>`,
})
export class Evaluation {}
