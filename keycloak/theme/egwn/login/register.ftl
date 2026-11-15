<#import "template.ftl" as layout>
<#import "user-profile-commons.ftl" as userProfile>
<#import "register-commons.ftl" as registration>
<@layout.registrationLayout displayMessage=messagesPerField.exists('global') displayRequiredFields=true; section>
  <#if section = "header">
    ${msg("registerTitle")}
  <#elseif section = "form">
    <form id="kc-register-form" class="${properties.kcFormClass!}" action="${url.registrationAction}" method="post">
      <@userProfile.userProfileFormFields; callback, attribute></@userProfile.userProfileFormFields>
      <#if passwordRequired??>
        <div class="${properties.kcFormGroupClass!}">
          <div class="${properties.kcLabelWrapperClass!}">
            <label for="password" class="${properties.kcLabelClass!}">${msg("password")}</label>
            <span class="required">*</span>
          </div>
          <div class="${properties.kcInputWrapperClass!}">
            <input type="password" id="password" name="password" class="${properties.kcInputClass!}" autocomplete="new-password" required
                   aria-invalid="${messagesPerField.existsError('password')?c}"/>
            <#if messagesPerField.existsError('password')>
              <span class="${properties.kcInputErrorMessageClass!}">${kcSanitize(messagesPerField.get('password'))?no_esc}</span>
            </#if>
          </div>
        </div>
        <div class="${properties.kcFormGroupClass!}">
          <div class="${properties.kcLabelWrapperClass!}">
            <label for="password-confirm" class="${properties.kcLabelClass!}">${msg("passwordConfirm")}</label>
            <span class="required">*</span>
          </div>
          <div class="${properties.kcInputWrapperClass!}">
            <input type="password" id="password-confirm" name="password-confirm" class="${properties.kcInputClass!}" autocomplete="new-password" required
                   aria-invalid="${messagesPerField.existsError('password-confirm')?c}"/>
            <#if messagesPerField.existsError('password-confirm')>
              <span class="${properties.kcInputErrorMessageClass!}">${kcSanitize(messagesPerField.get('password-confirm'))?no_esc}</span>
            </#if>
          </div>
        </div>
      </#if>
      <div class="${properties.kcFormGroupClass!}">
        <div class="${properties.kcLabelWrapperClass!}">
          <label for="egwn_registration_code" class="${properties.kcLabelClass!}">${msg("egwnRegistrationCode")}</label>
          <span class="required">*</span>
        </div>
        <div class="${properties.kcInputWrapperClass!}">
          <input type="password" id="egwn_registration_code" name="egwn_registration_code" class="${properties.kcInputClass!}"
                 autocomplete="off" maxlength="512" required
                 aria-invalid="${messagesPerField.existsError('egwn_registration_code')?c}"
                 <#if messagesPerField.existsError('egwn_registration_code')>aria-describedby="egwn-code-error"</#if>/>
          <#if messagesPerField.existsError('egwn_registration_code')>
            <span id="egwn-code-error" class="${properties.kcInputErrorMessageClass!}" aria-live="polite">${kcSanitize(messagesPerField.get('egwn_registration_code'))?no_esc}</span>
          </#if>
        </div>
      </div>
      <@registration.termsAcceptance/>
      <#if recaptchaRequired??>
        <div class="${properties.kcFormGroupClass!}">
          <div class="${properties.kcInputWrapperClass!}">
            <div class="g-recaptcha" data-sitekey="${recaptchaSiteKey}" data-action="${recaptchaAction}" data-size="compact"></div>
          </div>
        </div>
      </#if>
      <div class="${properties.kcFormGroupClass!}">
        <div class="${properties.kcFormButtonsClass!}">
          <input class="${properties.kcButtonClass!} ${properties.kcButtonPrimaryClass!} ${properties.kcButtonBlockClass!}" type="submit" value="${msg('doRegister')}"/>
          <a href="${url.loginUrl}">${msg("backToLogin")}</a>
        </div>
      </div>
    </form>
  </#if>
</@layout.registrationLayout>
