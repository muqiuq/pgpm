/**
 * `input-props` for PrimeVue's Password without its strength popup (`:feedback="false"`). The component still writes
 * `aria-expanded` and `aria-haspopup` on the input, which a password input (no ARIA role) may not carry – axe's
 * `aria-allowed-attr`, a critical finding (docs/03 §12.10). `inputProps` are merged last, so `undefined` removes them.
 */
export function passwordInputProps(
  autocomplete: 'current-password' | 'new-password',
  extra: Record<string, string> = {},
): Record<string, string | undefined> {
  return { autocomplete, 'aria-expanded': undefined, 'aria-haspopup': undefined, ...extra }
}
