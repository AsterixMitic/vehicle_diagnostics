import { signalName, unitLabel } from './signals';

describe('signals', () => {
  it('names known signals in Serbian and leaves unknown ones as identifiers', () => {
    expect(signalName('engine_rpm')).toBe('Број обртаја мотора');
    expect(signalName('custom_signal')).toBe('custom_signal');
  });

  it('shows units as written in the thesis', () => {
    expect(unitLabel('degC')).toBe('°C');
    expect(unitLabel('km_h')).toBe('km/h');
    expect(unitLabel('V')).toBe('V');
  });
});
