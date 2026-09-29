/**
 * Serbian names of the signals the backend decodes (SAE J1979 Mode 01) and readable units.
 * The identifier stays visible next to the name, so the interface and the thesis use the same
 * terms; an unknown signal is shown by its identifier alone.
 */
const SIGNAL_NAMES: Readonly<Record<string, string>> = {
  engine_load: 'Оптерећење мотора',
  coolant_temp: 'Температура расхладне течности',
  intake_pressure: 'Притисак у усисној грани',
  engine_rpm: 'Број обртаја мотора',
  vehicle_speed: 'Брзина возила',
  intake_temp: 'Температура усисног ваздуха',
  maf_rate: 'Проток ваздуха (MAF)',
  throttle_position: 'Положај лептира гаса',
  fuel_level: 'Ниво горива',
  barometric_pressure: 'Атмосферски притисак',
  module_voltage: 'Напон управљачког модула',
  ambient_temp: 'Спољна температура',
  oil_temp: 'Температура уља',
  fuel_rate: 'Потрошња горива',
};

const UNITS: Readonly<Record<string, string>> = {
  degC: '°C',
  km_h: 'km/h',
  rpm: 'min⁻¹',
  percent: '%',
  g_s: 'g/s',
  L_h: 'L/h',
};

export function signalName(identifier: string): string {
  return SIGNAL_NAMES[identifier] ?? identifier;
}

export function unitLabel(unit: string): string {
  return UNITS[unit] ?? unit;
}
