export function assertNever(value: never): never {
  throw new UnreachableVariantError(value)
}

class UnreachableVariantError extends Error {
  readonly value: unknown

  constructor(value: unknown) {
    super("unreachable variant")
    this.name = "UnreachableVariantError"
    this.value = value
  }
}
