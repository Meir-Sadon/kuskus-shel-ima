import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { verificationApi } from '../api/site'
import type { FieldErrors } from '../api/client'
import { FieldError } from '../admin/ui'
import { useErrorMessage } from '../admin/hooks'
import { normalizePhone } from './phone'

export interface Verified {
  /** The phone in local form, "0501234567". */
  phone: string
  token: string
}

interface Props {
  phone: string
  onPhoneChange: (phone: string) => void
  verified: Verified | null
  onVerified: (verified: Verified) => void
  errors: FieldErrors
}

/** The phone field with its WhatsApp confirmation: send a code, type it, done. */
export function PhoneVerification({ phone, onPhoneChange, verified, onVerified, errors }: Props) {
  const { t } = useTranslation()
  const errorMessage = useErrorMessage()
  const [sent, setSent] = useState(false)
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ text: string; error?: boolean } | null>(null)

  const normalized = normalizePhone(phone)
  const isVerified = normalized !== null && verified?.phone === normalized

  async function sendCode() {
    if (!normalized) return
    setBusy(true)
    setMessage(null)
    try {
      const reply = await verificationApi.send(normalized)
      setSent(true)
      setCode('')
      setMessage({ text: reply?.devCode ? t('order.testCode', { code: reply.devCode }) : t('order.codeSent') })
    } catch (err) {
      setMessage({ text: errorMessage(err), error: true })
    } finally {
      setBusy(false)
    }
  }

  async function confirmCode() {
    if (!normalized) return
    setBusy(true)
    setMessage(null)
    try {
      const { token } = await verificationApi.confirm(normalized, code)
      onVerified({ phone: normalized, token })
      setSent(false)
      setMessage(null)
    } catch (err) {
      setMessage({ text: errorMessage(err), error: true })
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="stack">
      <span className="field">
        <label htmlFor="order-phone">{t('order.phone')}</label>
        <input
          id="order-phone"
          type="tel"
          inputMode="tel"
          autoComplete="tel"
          dir="ltr"
          value={phone}
          aria-describedby="order-phone-error order-phone-status"
          onChange={(e) => {
            onPhoneChange(e.target.value)
            setSent(false)
            setMessage(null)
          }}
        />
        <FieldError errors={errors} field="phone" id="order-phone-error" />
      </span>

      {isVerified ? (
        <p id="order-phone-status" className="form-status">
          {t('order.phoneVerified')}
        </p>
      ) : (
        <div className="row row--end">
          {!sent && (
            <button type="button" className="button-quiet" disabled={!normalized || busy} onClick={sendCode}>
              {t('order.sendCode')}
            </button>
          )}
          {sent && (
            <>
              <span className="field field--narrow">
                <label htmlFor="order-code">{t('order.code')}</label>
                <input
                  id="order-code"
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  dir="ltr"
                  maxLength={6}
                  value={code}
                  onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))}
                />
              </span>
              <button type="button" disabled={code.length !== 6 || busy} onClick={confirmCode}>
                {t('order.confirmCode')}
              </button>
              <button type="button" className="button-quiet" disabled={busy} onClick={sendCode}>
                {t('order.resendCode')}
              </button>
            </>
          )}
        </div>
      )}
      <p id={isVerified ? undefined : 'order-phone-status'} role={message?.error ? 'alert' : 'status'} className={message?.error ? 'form-error' : 'form-status'}>
        {message?.text}
      </p>
    </div>
  )
}
