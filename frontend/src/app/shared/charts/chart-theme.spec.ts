import { baseOptions, CHART_CHROME, modeColor } from './chart-theme';

describe('chart theme', () => {
  it('gives each mode a fixed color per scheme', () => {
    expect(modeColor('P0', 'light')).toBe('#2a78d6');
    expect(modeColor('P2', 'light')).toBe('#1baf7a');
    expect(modeColor('P2', 'dark')).toBe('#199e70');
  });

  it('keeps unknown neutral in both schemes', () => {
    expect(modeColor('unknown', 'light')).toBe(modeColor('unknown', 'dark'));
  });

  it('writes text in ink tokens of the scheme', () => {
    expect(baseOptions('dark').textStyle.color).toBe(CHART_CHROME.dark.secondary);
    expect(baseOptions('light').textStyle.color).toBe(CHART_CHROME.light.secondary);
  });
});
