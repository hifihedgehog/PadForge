// Copyright 2020-2021, N Madsen.
// Copyright 2020-2023, Collabora, Ltd.
// Copyright 2020-2023, Jan Schmidt
// SPDX-License-Identifier: BSL-1.0
/*!
 * @file
 * @brief  Driver for WMR Controllers.
 * @author Jan Schmidt <jan@centricular.com>
 * @ingroup drv_wmr
 */
#include <assert.h>

#include "wmr_common.h"
#include "wmr_controller.h"

struct wmr_controller_base *
wmr_controller_og_create(struct wmr_controller_connection *conn,
                         enum xrt_device_type controller_type,
                         uint16_t pid,
                         enum u_logging_level log_level);

struct wmr_controller_base *
wmr_controller_hp_create(struct wmr_controller_connection *conn,
                         enum xrt_device_type controller_type,
                         enum u_logging_level log_level);

struct wmr_controller_base *
wmr_controller_create(struct wmr_controller_connection *conn,
                      enum xrt_device_type controller_type,
                      uint16_t vid,
                      uint16_t pid,
                      enum u_logging_level log_level)
{
	struct wmr_controller_base *ret = NULL;

	assert(vid == MICROSOFT_VID); /* The only known controllers all use Microsoft VID right now */

	switch (pid) {
	case WMR_CONTROLLER_PID:
	case ODYSSEY_CONTROLLER_PID: ret = wmr_controller_og_create(conn, controller_type, pid, log_level); break;
	case REVERB_G2_CONTROLLER_PID: ret = wmr_controller_hp_create(conn, controller_type, log_level); break;
	default: break;
	}

	return ret;
}
